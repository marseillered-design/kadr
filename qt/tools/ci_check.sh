#!/usr/bin/env bash
# Runs a check; when it fails, its last output becomes a GitHub annotation, which anyone can read
# (job logs need a GitHub sign-in). Usage: bash tools/ci_check.sh "Title" command args...
title="$1"; shift
log="$(mktemp)"
"$@" 2>&1 | tee "$log"
code=${PIPESTATUS[0]}
if [ "$code" -ne 0 ]; then
  msg="$(tail -c 1800 "$log" | tr '\r\n' '  ' | sed 's/%/%25/g')"
  echo "::error title=$title (exit $code)::$msg"
fi
exit "$code"
