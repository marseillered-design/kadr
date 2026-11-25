"""The computer's sound on macOS 13+ through ScreenCaptureKit (macOS has no other way to hear what apps play).
An audio-only stream (with the smallest possible picture, which SCK requires) delivers float samples on its own
queue; they are interleaved to stereo and handed to the recorder's mixer. Needs the Screen Recording permission,
which Kadr asks for anyway."""
import threading

import objc
from Foundation import NSObject

import CoreMedia
import ScreenCaptureKit as SCK

from .recorder import PcmSource

SCStreamOutput = objc.protocolNamed("SCStreamOutput")


class _Output(NSObject, protocols=[SCStreamOutput]):
    def initWithOwner_(self, owner):
        self = objc.super(_Output, self).init()
        if self is None:
            return None
        self.owner = owner
        return self

    def stream_didOutputSampleBuffer_ofType_(self, stream, sbuf, kind):
        if kind != SCK.SCStreamOutputTypeAudio:
            return
        try:
            self.owner._sample(sbuf)
        except Exception:
            pass


class SystemAudio(PcmSource):
    def __init__(self, rate):
        super().__init__()
        self.lock = threading.Lock()
        done, got = threading.Event(), {}

        def content(c, err):
            got["c"] = c
            done.set()

        SCK.SCShareableContent.getShareableContentWithCompletionHandler_(content)
        if not done.wait(5) or got.get("c") is None or not got["c"].displays():
            raise RuntimeError("no screen recording permission")
        flt = SCK.SCContentFilter.alloc().initWithDisplay_excludingWindows_(got["c"].displays()[0], [])
        cfg = SCK.SCStreamConfiguration.alloc().init()
        cfg.setCapturesAudio_(True)
        cfg.setExcludesCurrentProcessAudio_(True)
        cfg.setSampleRate_(rate)
        cfg.setChannelCount_(2)
        cfg.setWidth_(2)
        cfg.setHeight_(2)
        cfg.setMinimumFrameInterval_(CoreMedia.CMTimeMake(1, 1))
        self.out = _Output.alloc().initWithOwner_(self)
        self.stream = SCK.SCStream.alloc().initWithFilter_configuration_delegate_(flt, cfg, None)
        ok = self.stream.addStreamOutput_type_sampleHandlerQueue_error_(self.out, SCK.SCStreamOutputTypeAudio, None, None)
        if isinstance(ok, tuple):
            ok = ok[0]
        if not ok:
            raise RuntimeError("audio output refused")
        started, res = threading.Event(), {}

        def on_start(err):
            res["err"] = err
            started.set()

        self.stream.startCaptureWithCompletionHandler_(on_start)
        if not started.wait(5) or res.get("err") is not None:
            raise RuntimeError(str(res.get("err") or "start timed out"))

    def _sample(self, sbuf):
        frames = CoreMedia.CMSampleBufferGetNumSamples(sbuf)
        bb = CoreMedia.CMSampleBufferGetDataBuffer(sbuf)
        if not frames or bb is None:
            return
        length = CoreMedia.CMBlockBufferGetDataLength(bb)
        r = CoreMedia.CMBlockBufferCopyDataBytes(bb, 0, length, None)
        data = r[1] if isinstance(r, tuple) else r
        if not data:
            return
        data = bytes(data)
        from array import array
        a = array("f")
        a.frombytes(data[: len(data) // 4 * 4])
        if len(a) >= frames * 2:
            # SCK hands out planar float (all left samples, then all right): interleave them
            left, right = a[:frames], a[frames:2 * frames]
            st = array("f", (0.0,)) * (frames * 2)
            st[0::2] = left
            st[1::2] = right
        else:
            st = array("f", (0.0,)) * (len(a) * 2)
            st[0::2] = a
            st[1::2] = a
        with self.lock:
            self.buf.extend(st)

    def poll(self, drop):
        if drop:
            with self.lock:
                del self.buf[:]

    def mix_into(self, mix, n):
        with self.lock:
            PcmSource.mix_into(self, mix, n)

    def stop(self):
        done = threading.Event()
        try:
            self.stream.stopCaptureWithCompletionHandler_(lambda err: done.set())
            done.wait(3)
        except Exception:
            pass
