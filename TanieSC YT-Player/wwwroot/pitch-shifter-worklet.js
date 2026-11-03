/* =========================================================================
   SoundTouch pitch-shifter AudioWorklet.
   Uses the SoundTouchJS core (Stretch + RateTransposer) directly.
   Input and output sample rate are identical; only pitch changes.
   Tempo is handled natively via <audio>.playbackRate + preservesPitch.

   A small stereo jitter buffer absorbs SoundTouch's bursty output so the
   render quantum always has samples ready to deliver. On the rare underrun
   after priming, we hold the last sample instead of zero-filling — that
   avoids the discontinuity click.
   ========================================================================= */
import { SoundTouch } from '/soundtouch-core.js';

class SoundTouchProcessor extends AudioWorkletProcessor {
    static get parameterDescriptors() {
        return [{
            name: 'pitch',
            defaultValue: 1.0,
            minValue: 0.25,
            maxValue: 4.0,
            automationRate: 'k-rate'
        }];
    }

    constructor() {
        super();

        this._st = new SoundTouch();

        try {
           // this._st.stretch.setParameters(sampleRate, 0, 0, 0);
           this._st.stretch.setParameters(sampleRate, 40, 15, 8);
        } catch (_) {}

        this._st.pitch = 1.0;
        this._st.tempo = 1.0;
        this._st.rate  = 1.0;
        this._lastPitch = 1.0;

        // Scratch buffers for interleaved I/O at block size N.
        this._inBuf  = new Float32Array(256 * 2);
        this._outBuf = new Float32Array(256 * 2);

        // ── Jitter buffer (stereo interleaved ring) ──────────────────
        // Sized in *frames*; indices are in floats (2 per frame).
        // 4096 frames ≈ 85 ms @ 48 kHz — comfortably above any burst.
        this._jitterCapacity = 4096;
        this._jitter      = new Float32Array(this._jitterCapacity * 2);
        this._jitterRead  = 0;     // float index
        this._jitterWrite = 0;     // float index
        this._jitterCount = 0;     // frames currently stored

        // Pre-prime threshold: how many frames we want buffered before we
        // start delivering. 512 frames ≈ 10.7 ms @ 48 kHz — enough to
        // absorb the first burst without adding perceptible latency.
        this._targetFill = 512;
        this._primed = false;

        // Last frame delivered, used as the hold value on underrun.
        this._lastL = 0;
        this._lastR = 0;
    }

    _ensureInput(n) {
        if (this._inBuf.length  < n * 2) this._inBuf  = new Float32Array(n * 2);
        if (this._outBuf.length < n * 2) this._outBuf = new Float32Array(n * 2);
    }

    _jitterPush(interleaved, frames) {
        // Never overflow; if it would, drop the excess (should not happen
        // given the consumer pulls every block).
        const free = this._jitterCapacity - this._jitterCount;
        const toWrite = Math.min(frames, free);

        const cap2 = this._jitterCapacity * 2;
        for (let i = 0; i < toWrite; i++) {
            this._jitter[this._jitterWrite]     = interleaved[i * 2];
            this._jitter[this._jitterWrite + 1] = interleaved[i * 2 + 1];
            this._jitterWrite = (this._jitterWrite + 2) % cap2;
            this._jitterCount++;
        }
    }

    _jitterPull(out, frames) {
        const canRead = Math.min(frames, this._jitterCount);
        const cap2 = this._jitterCapacity * 2;
        for (let i = 0; i < canRead; i++) {
            out[i * 2]     = this._jitter[this._jitterRead];
            out[i * 2 + 1] = this._jitter[this._jitterRead + 1];
            this._jitterRead = (this._jitterRead + 2) % cap2;
            this._jitterCount--;
        }
        return canRead;
    }

    process(inputs, outputs, params) {
        const input = inputs[0];
        if (!input || input.length === 0) return true;

        const inL  = input[0];
        const inR  = input[1] || input[0];
        const outL = outputs[0][0];
        const outR = outputs[0][1] || outputs[0][0];
        const N    = outL.length;

        // ── Parameter update ─────────────────────────────────────────
        const p = params.pitch[0];
        if (p !== this._lastPitch) {
            this._st.pitch = p;
            this._lastPitch = p;
        }

        this._ensureInput(N);

        // ── Interleave + push N frames of input ──────────────────────
        const inBuf = this._inBuf;
        for (let i = 0; i < N; i++) {
            inBuf[i * 2]     = inL[i];
            inBuf[i * 2 + 1] = inR[i];
        }
        this._st.inputBuffer.putSamples(inBuf, 0, N);

        // ── Run SoundTouch and drain everything it produced into jitter ─
        // One process() call consumes as many input frames as SoundTouch
        // needs, up to what we've queued. Drain every sample it hands back
        // so the jitter buffer accumulates bursts.
        this._st.process();

        const outBuf = this._outBuf;
        const outCapFrames = outBuf.length / 2;
        while (true) {
            const available = this._st.outputBuffer.frameCount;
            if (available <= 0) break;

            const chunk = Math.min(available, outCapFrames);
            this._st.outputBuffer.receiveSamples(outBuf, chunk);
            this._jitterPush(outBuf, chunk);
        }

        // ── Pre-prime: hold silence until the jitter buffer is stocked ─
        // This only runs at the very start of a stream — the audio hasn't
        // begun yet, so silence here is not a click.
        if (!this._primed) {
            if (this._jitterCount >= this._targetFill) {
                this._primed = true;
            } else {
                outL.fill(0);
                outR.fill(0);
                return true;
            }
        }

        // ── Deliver N frames from the jitter buffer ──────────────────
        const delivered = this._jitterPull(outBuf, N);

        for (let i = 0; i < delivered; i++) {
            outL[i] = outBuf[i * 2];
            outR[i] = outBuf[i * 2 + 1];
        }

        if (delivered > 0) {
            this._lastL = outL[delivered - 1];
            this._lastR = outR[delivered - 1];
        }

        // ── Underrun: hold last sample, no zero-fill ─────────────────
        // Should be extremely rare after priming; this is the safety net
        // that turns a click into an inaudible micro-hold.
        if (delivered < N) {
            for (let i = delivered; i < N; i++) {
                outL[i] = this._lastL;
                outR[i] = this._lastR;
            }
        }

        return true;
    }
}

registerProcessor('pitch-shifter', SoundTouchProcessor);