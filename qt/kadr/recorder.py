"""Screen recording for macOS and Linux (the Windows version has its own, src/Recorder.cs).

Picture: Qt's QScreenCapture (X11, macOS; Wayland through the ScreenCast portal + PipeWire) hands over
whole-screen frames; the chosen area is cut out and fed into a second capture session through
QVideoFrameInput, whose QMediaRecorder writes an MP4 (H.264 + AAC with Qt's FFmpeg backend).
Sound: the microphone through QAudioSource, the computer's sound through the PulseAudio/PipeWire
"monitor" (Linux) or ScreenCaptureKit (macOS 13+, kadr/macaudio.py); both are mixed here and fed
through QAudioBufferInput. One clock times picture and sound and stops while paused."""
import struct
import subprocess
import sys
import time
from array import array

from PySide6.QtCore import QByteArray, QElapsedTimer, QObject, QRect, QSize, Qt, QTimer, QUrl, Signal
from PySide6.QtGui import QImage
from PySide6.QtMultimedia import (QAudioBuffer, QAudioBufferInput, QAudioFormat, QAudioSource, QMediaCaptureSession,
                                  QMediaDevices, QMediaFormat, QMediaRecorder, QScreenCapture, QVideoFrame,
                                  QVideoFrameFormat, QVideoFrameInput, QVideoSink)

RATE, CHANNELS = 48000, 2


def even(v):
    return max(2, int(v) // 2 * 2)


class Clock:
    """Microseconds of recording time; stands still while paused."""

    def __init__(self):
        self.t = QElapsedTimer()
        self.base = 0
        self.running = False

    def start(self):
        self.t.start()
        self.running = True

    def pause(self):
        if self.running:
            self.base += self.t.nsecsElapsed() // 1000
            self.running = False

    def resume(self):
        if not self.running:
            self.t.restart()
            self.running = True

    def us(self):
        return self.base + (self.t.nsecsElapsed() // 1000 if self.running else 0)


class PcmSource:
    """Collects 48 kHz stereo float samples from one device; mix_into() takes what is there, silence for the rest."""

    def __init__(self):
        self.buf = array("f")

    def poll(self, drop):
        pass

    def push(self, data: bytes):
        a = array("f")
        a.frombytes(data[: len(data) // 4 * 4])
        self.buf.extend(a)

    def mix_into(self, mix, n):
        take = min(n * 2, len(self.buf))
        for i in range(take):
            mix[i] += self.buf[i]
        del self.buf[:take]
        keep = RATE // 4 * 2   # more than 0.25 s waiting: this device runs ahead, drop the surplus
        if len(self.buf) > keep:
            del self.buf[: len(self.buf) - keep]

    def stop(self):
        pass


def float_format():
    f = QAudioFormat()
    f.setSampleRate(RATE)
    f.setChannelCount(CHANNELS)
    f.setSampleFormat(QAudioFormat.SampleFormat.Float)
    return f


class QtSource(PcmSource):
    """A Qt audio input (microphone, or a monitor device on Linux)."""

    def __init__(self, device):
        super().__init__()
        fmt = float_format()
        if not device.isFormatSupported(fmt):
            fmt = device.preferredFormat()
        self.fmt = fmt
        self.src = QAudioSource(device, fmt)
        self.src.setBufferSize(RATE * 8 // 5)
        self.io = self.src.start()
        if self.io is None:
            raise RuntimeError("audio device did not start")

    def poll(self, drop):
        data = bytes(self.io.readAll().data())
        if drop or not data:
            return
        self.push(to_float_stereo(data, self.fmt))

    def stop(self):
        self.src.stop()


def to_float_stereo(data: bytes, fmt: QAudioFormat) -> bytes:
    """Whatever the device gives (int16/int32/float, mono/stereo, any rate) → 48 kHz stereo float32."""
    sf = fmt.sampleFormat()
    ch = max(1, fmt.channelCount())
    if sf == QAudioFormat.SampleFormat.Float:
        s = array("f"); s.frombytes(data[: len(data) // 4 * 4])
    elif sf == QAudioFormat.SampleFormat.Int16:
        i = array("h"); i.frombytes(data[: len(data) // 2 * 2])
        s = array("f", (x / 32768.0 for x in i))
    elif sf == QAudioFormat.SampleFormat.Int32:
        i = array("i"); i.frombytes(data[: len(data) // 4 * 4])
        s = array("f", (x / 2147483648.0 for x in i))
    else:   # UInt8
        s = array("f", ((b - 128) / 128.0 for b in data))
    frames = len(s) // ch
    if ch == 1:
        st = array("f", (0.0,)) * (frames * 2)
        st[0::2] = s[:frames]
        st[1::2] = s[:frames]
    else:
        st = array("f", (0.0,)) * (frames * 2)
        st[0::2] = s[0::ch][:frames]
        st[1::2] = s[1::ch][:frames]
    rate = fmt.sampleRate()
    if rate and rate != RATE:
        n = int(frames * RATE / rate)
        out = array("f", (0.0,)) * (n * 2)
        k = rate / RATE
        for j in range(n):
            src = min(frames - 1, int(j * k))
            out[2 * j] = st[2 * src]
            out[2 * j + 1] = st[2 * src + 1]
        st = out
    return st.tobytes()


class ProcessSource(PcmSource):
    """The computer's sound on Linux when Qt does not list the monitor: `parec` (PulseAudio and PipeWire-Pulse)
    or `pw-record` writes raw float samples to a pipe that is read without blocking."""

    def __init__(self, cmd):
        super().__init__()
        import fcntl
        import os
        self.p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        fd = self.p.stdout.fileno()
        fcntl.fcntl(fd, fcntl.F_SETFL, fcntl.fcntl(fd, fcntl.F_GETFL) | os.O_NONBLOCK)
        time.sleep(0.15)
        if self.p.poll() is not None:
            raise RuntimeError("monitor capture did not start")

    def poll(self, drop):
        try:
            data = self.p.stdout.read()
        except (BlockingIOError, TypeError):
            data = None
        if data and not drop:
            self.push(data)

    def stop(self):
        try:
            self.p.terminate()
            self.p.wait(2)
        except Exception:
            pass


def system_sound_source():
    """The computer's sound: Linux = monitor of the default output; macOS 13+ = ScreenCaptureKit; else None."""
    if sys.platform == "darwin":
        try:
            from . import macaudio
            return macaudio.SystemAudio(RATE)
        except Exception:
            return None
    if not sys.platform.startswith("linux"):
        return None
    for d in QMediaDevices.audioInputs():
        if "monitor" in (d.description() + " " + bytes(d.id().data()).decode("utf-8", "replace")).lower():
            default_out = QMediaDevices.defaultAudioOutput().description()
            if default_out and default_out.lower() in d.description().lower():
                try:
                    return QtSource(d)
                except Exception:
                    break
    import shutil
    for cmd in (["parec", "-d", "@DEFAULT_MONITOR@", "--format=float32le", f"--rate={RATE}", f"--channels={CHANNELS}", "--latency-msec=40"],
                ["pw-record", "--target", "@DEFAULT_MONITOR@", "--format", "f32", "--rate", str(RATE), "--channels", str(CHANNELS), "-"]):
        if shutil.which(cmd[0]):
            try:
                return ProcessSource(cmd)
            except Exception:
                continue
    return None


def microphone_source():
    dev = QMediaDevices.defaultAudioInput()
    if dev.isNull():
        return None
    try:
        return QtSource(dev)
    except Exception:
        return None


def choose_format(audio: bool) -> QMediaFormat:
    """MP4 with H.264 + AAC where the system has an H.264 encoder (macOS: VideoToolbox; Linux: whatever Qt's FFmpeg
    offers); otherwise WebM with VP9/VP8 + Opus, which every browser and chat app plays too."""
    mp4 = QMediaFormat(QMediaFormat.FileFormat.MPEG4)
    enc = QMediaFormat.ConversionMode.Encode
    if QMediaFormat.VideoCodec.H264 in mp4.supportedVideoCodecs(enc):
        mp4.setVideoCodec(QMediaFormat.VideoCodec.H264)
        if audio:
            mp4.setAudioCodec(QMediaFormat.AudioCodec.AAC)
        return mp4
    webm = QMediaFormat(QMediaFormat.FileFormat.WebM)
    vc = webm.supportedVideoCodecs(enc)
    for c in (QMediaFormat.VideoCodec.VP9, QMediaFormat.VideoCodec.VP8, QMediaFormat.VideoCodec.AV1):
        if c in vc:
            webm.setVideoCodec(c)
            if audio:
                webm.setAudioCodec(QMediaFormat.AudioCodec.Opus)
            return webm
    mp4.setVideoCodec(QMediaFormat.VideoCodec.MPEG4)   # last resort: plays nearly everywhere, bigger files
    if audio:
        mp4.setAudioCodec(QMediaFormat.AudioCodec.AAC)
    return mp4


class Recorder(QObject):
    """Records `area` (logical pixels on `screen`) into `path`. Signals arrive on the GUI thread."""
    finished = Signal(bool, str)   # ok, error text
    failed = Signal(str)

    def __init__(self, screen, area: QRect, path: str, fps=30, sys_audio=True, mic=False, test_frames=None):
        super().__init__()
        self.screen, self.area, self.path, self.fps = screen, QRect(area), path, max(5, min(60, int(fps)))
        self.want_sys, self.want_mic = sys_audio, mic
        self.sys = self.mic = None
        self.clock = Clock()
        self.paused = False
        self.frames = 0
        self.thumb = None
        self.error = ""
        self.stopping = False
        self.test_frames = test_frames   # build checks: a callable giving a QImage, instead of the screen
        dpr = screen.devicePixelRatio() if screen is not None else 1.0
        w, h = area.width() * dpr, area.height() * dpr
        k = min(1.0, 3840 / w, 2160 / h)    # H.264: at most 4K
        self.size = QSize(even(w * k), even(h * k))
        self.next_frame_us = 0
        self.audio_written = 0

    # ---- start / stop ----
    def start(self):
        if self.want_sys:
            self.sys = system_sound_source()
        if self.want_mic:
            self.mic = microphone_source()
        self.out = QMediaCaptureSession()
        fmt = QVideoFrameFormat(self.size, QVideoFrameFormat.PixelFormat.Format_BGRA8888)
        fmt.setStreamFrameRate(self.fps)
        self.vin = QVideoFrameInput(fmt)
        self.out.setVideoFrameInput(self.vin)
        self.ain = None
        if self.sys is not None or self.mic is not None:
            self.ain = QAudioBufferInput(float_format())
            self.out.setAudioBufferInput(self.ain)
        self.rec = QMediaRecorder()
        self.out.setRecorder(self.rec)
        mf = choose_format(self.ain is not None)
        if mf.fileFormat() == QMediaFormat.FileFormat.WebM and self.path.endswith(".mp4"):
            self.path = self.path[:-4] + ".webm"
        self.rec.setMediaFormat(mf)
        self.rec.setQuality(QMediaRecorder.Quality.HighQuality)
        self.rec.setVideoResolution(self.size)
        self.rec.setVideoFrameRate(self.fps)
        self.rec.setVideoBitRate(int(max(1_500_000, min(24_000_000, self.size.width() * self.size.height() * self.fps * 0.1))))
        self.rec.setOutputLocation(QUrl.fromLocalFile(self.path))
        self.rec.errorOccurred.connect(self._rec_error)
        self.rec.recorderStateChanged.connect(self._state)
        self.rec.setAutoStop(False)
        self.rec.record()

        if self.test_frames is None:
            self.cap = QScreenCapture()
            self.cap.setScreen(self.screen)
            self.cap.errorOccurred.connect(lambda e, s: self._fail(s or str(e)))
            self.src = QMediaCaptureSession()
            self.src.setScreenCapture(self.cap)
            self.sink = QVideoSink()
            self.src.setVideoSink(self.sink)
            self.sink.videoFrameChanged.connect(self._frame)
            self.cap.start()
        # A steady clock sends the newest picture fps times a second: screen captures only deliver frames when
        # something changes, and a still screen must still fill the video up to the stop.
        self.latest = None
        self.latest_new = False
        self.last_img = None
        self.tick = QTimer(self)
        self.tick.setTimerType(Qt.TimerType.PreciseTimer)
        self.tick.timeout.connect(self._tick)
        self.tick.start(1000 // self.fps)
        self.audio_timer = QTimer(self)
        self.audio_timer.timeout.connect(self._audio)
        self.audio_timer.start(20)
        self.clock.start()

    def set_paused(self, p):
        if p == self.paused:
            return
        self.paused = p
        if p:
            self.clock.pause()
        else:
            self.clock.resume()

    def elapsed_seconds(self):
        return self.clock.us() / 1e6

    def stop(self):
        """Finishes the file; `finished` follows when the recorder has written it."""
        if self.stopping:
            return
        self.stopping = True
        self._audio(final=True)
        self.audio_timer.stop()
        self.tick.stop()
        if self.last_img is not None and not self.paused:
            self._send(self.last_img, self.clock.us())   # the last picture lasts until the stop
        if self.test_frames is None:
            try:
                self.cap.stop()
            except Exception:
                pass
        for s in (self.sys, self.mic):
            if s is not None:
                s.stop()
        self.clock.pause()
        self.rec.stop()
        if self.rec.recorderState() == QMediaRecorder.RecorderState.StoppedState:
            QTimer.singleShot(0, self._done)

    # ---- picture ----
    def _frame(self, frame: QVideoFrame):
        if frame.isValid():
            self.latest = frame        # converted on the next tick only
            self.latest_new = True

    def _tick(self):
        if self.paused or self.stopping:
            return
        if self.test_frames is not None:
            self.last_img = self._fit(self.test_frames())
        elif self.latest_new:
            self.latest_new = False
            img = self.latest.toImage()
            if not img.isNull():
                # the frame covers the whole screen, maybe in physical pixels (Retina, Wayland scaling)
                geo = self.screen.geometry()
                kx, ky = img.width() / max(1, geo.width()), img.height() / max(1, geo.height())
                r = QRect(round((self.area.x() - geo.x()) * kx), round((self.area.y() - geo.y()) * ky),
                          round(self.area.width() * kx), round(self.area.height() * ky))
                self.last_img = self._fit(img.copy(r))
        if self.last_img is not None:
            self._send(self.last_img, self.clock.us())

    def _fit(self, img: QImage) -> QImage:
        if img.size() != self.size:
            img = img.scaled(self.size, Qt.AspectRatioMode.IgnoreAspectRatio, Qt.TransformationMode.SmoothTransformation)
        return img.convertToFormat(QImage.Format.Format_ARGB32)

    def _send(self, img: QImage, t):
        if t < self.next_frame_us - 1000:
            return
        vf = QVideoFrame(img)
        interval = 1_000_000 // self.fps
        vf.setStartTime(t)
        vf.setEndTime(t + interval)
        if self.vin.sendVideoFrame(vf):
            if self.frames == 0:
                self.thumb = img.scaled(400, 300, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
            self.frames += 1
            self.next_frame_us = t + interval // 2

    # ---- sound ----
    def _audio(self, final=False):
        if self.ain is None:
            return
        drop = self.paused
        for s in (self.sys, self.mic):
            if s is not None:
                try:
                    s.poll(drop)
                except Exception:
                    pass
        if drop:
            return
        target = self.clock.us() * RATE // 1_000_000 - (0 if final else RATE // 10)   # 0.1 s behind: devices have delivered
        while self.audio_written < target:
            n = int(min(target - self.audio_written, RATE // 2))
            mix = array("f", (0.0,)) * (n * 2)
            for s in (self.sys, self.mic):
                if s is not None:
                    s.mix_into(mix, n)
            for i in range(n * 2):
                v = mix[i]
                if v > 1.0:
                    mix[i] = 1.0
                elif v < -1.0:
                    mix[i] = -1.0
            buf = QAudioBuffer(QByteArray(mix.tobytes()), float_format(), self.audio_written * 1_000_000 // RATE)
            if not self.ain.sendAudioBuffer(buf):
                break   # the encoder is busy: try again on the next tick
            self.audio_written += n

    # ---- results ----
    def _rec_error(self, err, text):
        self._fail(text or str(err))

    def _fail(self, text):
        if self.error:
            return
        self.error = text
        self.failed.emit(text)

    def _state(self, st):
        if st == QMediaRecorder.RecorderState.StoppedState and self.stopping:
            self._done()

    def _done(self):
        if getattr(self, "_reported", False):
            return
        self._reported = True
        import os
        ok = not self.error and os.path.exists(self.path) and os.path.getsize(self.path) > 0
        self.finished.emit(ok, self.error)


def record_test(path: str, seconds: float = 3.0, sound=True) -> int:
    """Build check (works offscreen): moving pictures + a tone through the real encoder, then checks the file."""
    import math
    import os
    from PySide6.QtGui import QColor, QPainter
    from PySide6.QtWidgets import QApplication
    app = QApplication.instance() or QApplication(sys.argv)
    f = QMediaFormat(QMediaFormat.FileFormat.MPEG4)
    print("encoders: video", [c.name for c in f.supportedVideoCodecs(QMediaFormat.ConversionMode.Encode)],
          "audio", [c.name for c in f.supportedAudioCodecs(QMediaFormat.ConversionMode.Encode)])
    from PySide6.QtCore import QCoreApplication
    for d in QCoreApplication.libraryPaths():
        mm = os.path.join(d, "multimedia")
        print("plugins:", d, "multimedia:", sorted(os.listdir(mm)) if os.path.isdir(mm) else "none")
    chosen = choose_format(sound)
    print("chosen:", chosen.fileFormat().name, chosen.videoCodec().name, chosen.audioCodec().name)
    n = [0]

    def picture():
        img = QImage(640, 360, QImage.Format.Format_ARGB32)
        img.fill(QColor(30, 30, 40))
        p = QPainter(img)
        p.fillRect(40 + (n[0] * 7) % 500, 140, 80, 80, QColor(229, 57, 53))
        p.end()
        n[0] += 1
        return img

    rec = Recorder(None, QRect(0, 0, 640, 360), os.path.abspath(path), 30, False, False, test_frames=picture)
    if sound:
        class Tone(PcmSource):
            def __init__(self):
                super().__init__()
                self.i = 0

            def poll(self, drop):
                n2 = RATE // 50
                a = array("f", (0.0,)) * (n2 * 2)
                for j in range(n2):
                    v = 0.2 * math.sin(2 * math.pi * 440 * (self.i + j) / RATE)
                    a[2 * j] = a[2 * j + 1] = v
                self.i += n2
                if not drop:
                    self.buf.extend(a)
        rec.want_mic = True
        global microphone_source
        microphone_source = lambda: Tone()
    result = {}
    rec.finished.connect(lambda ok, err: (result.update(ok=ok, err=err), app.quit()))
    rec.failed.connect(lambda err: print("failed:", err))
    rec.start()
    QTimer.singleShot(int(seconds * 1000), rec.stop)
    QTimer.singleShot(int(seconds * 1000) + 20000, app.quit)
    app.exec()
    path = rec.path   # may have become .webm
    size = os.path.getsize(path) if os.path.exists(path) else 0
    print(f"record-test ok={result.get('ok')} err={result.get('err')!r} frames={rec.frames} bytes={size} audio_s={rec.audio_written / RATE:.2f}")
    return 0 if result.get("ok") and rec.frames > 10 and size > 1000 else 1
