/* =========================================================================
   SOUNDTOUCH PITCH-SHIFTER AUDIO WORKLET
   =========================================================================

   WHAT THIS FILE IS:
     A "worklet" — a small piece of JavaScript that runs on the browser's
     audio processing thread. It sits between the audio source (the
     <audio> element) and the speakers, and transforms every sample in
     real time.

   WHY A WORKLET AND NOT A NORMAL SCRIPT?
     Audio runs on a very tight deadline: 128 samples every ~2.9 ms.
     If the code that processes samples is too slow or gets paused by
     garbage collection or a modal dialog, you hear a click or a gap.
     The main thread (where UI code lives) can't guarantee that timing.
     Worklets run on a dedicated thread with a strict "finish before the
     next block" requirement, so they can.

   WHAT THIS PARTICULAR WORKLET DOES:
     Takes incoming audio, applies the SoundTouch pitch-shifting algorithm
     (which uses a technique called "time-stretching + resampling" to
     change the pitch while keeping the duration the same), and outputs
     the result.

   THE FLOW:
       <audio> element plays a track
             │
             ▼
       MediaElementSource ──► this worklet ──► destination (speakers)
                                  │
                                  └── SoundTouch does the DSP

   TEMPO vs PITCH:
     Tempo (playback speed) is handled NATIVELY by the <audio> element
     (setting .playbackRate). The worklet only touches pitch. Splitting
     it this way is simpler and lets the browser use its optimized
     time-stretcher for tempo while SoundTouch handles only what native
     audio can't do (independent pitch shifting).
   ========================================================================= */

// ─────────────────────────────────────────────────────────────────────
// IMPORT: load the SoundTouch DSP library from a sibling file.
//
// In a worklet you can't use <script src="..."> — you must use the
// modern ES module syntax. This line loads the file at the server
// root ("/soundtouch-core.js") as a module and pulls out the class
// named "SoundTouch".
//
// The file must be served with Content-Type: text/javascript (our
// StreamProxy does this), and it must be on the same origin
// (127.0.0.1) — otherwise the browser blocks the import.
// ─────────────────────────────────────────────────────────────────────
import { SoundTouch } from '/soundtouch-core.js';

/**
 * The main processor class. Every AudioWorklet file must define one,
 * and it must extend AudioWorkletProcessor.
 *
 * The browser creates ONE instance of this class per AudioWorkletNode
 * on the main thread. That instance lives for the whole page lifetime
 * — you don't get a fresh one per song.
 */
class SoundTouchProcessor extends AudioWorkletProcessor {

    /**
     * ── PARAMETER DESCRIPTORS ──
     *
     * This static method (called once by the browser when the worklet
     * is registered) declares what adjustable parameters this processor
     * has. Think of parameters like hardware knobs — the browser uses
     * this description to create them, and outside code can tweak them
     * at runtime.
     *
     * We declare ONE parameter: "pitch".
     *
     *   name:             the identifier used in JS to read/write it
     *                     (pitchNode.parameters.get('pitch'))
     *   defaultValue:     1.0 = no change
     *   minValue:         0.25 = two octaves down
     *   maxValue:         4.0  = two octaves up
     *   automationRate:   'k-rate' = "constant rate" — one value per
     *                     block of 128 samples. That's fine for pitch
     *                     because humans don't hear pitch changes
     *                     sample-by-sample; updating every 128 samples
     *                     is imperceptible.
     *
     * The alternative is 'a-rate' (audio-rate), which provides a
     * separate value for every sample. Overkill for this use case and
     * slightly slower.
     */
    static get parameterDescriptors() {
        return [{
            name: 'pitch',
            defaultValue: 1.0,
            minValue: 0.25,
            maxValue: 4.0,
            automationRate: 'k-rate'
        }];
    }

    /**
     * ── CONSTRUCTOR ──
     *
     * Runs ONCE when the node is created. Sets up the DSP engine and
     * allocates scratch buffers. No audio is being processed yet at
     * this point — that happens in process() below, called ~375 times
     * per second forever after.
     */
    constructor() {
        // Always call super() first — mandatory in a subclass
        // constructor. Without it, the object won't be fully set up.
        super();

        // ── Create the SoundTouch DSP engine ──
        // This is the "brain" that does the actual math. Its internal
        // structure (two signal-processing stages — a time-stretcher
        // and a resampler) is created lazily and configured below.
        this._st = new SoundTouch();

        // ── Tell SoundTouch the audio context's sample rate ──
        // SoundTouch needs to know this because it works with
        // millisecond-scale window sizes internally, and needs to
        // convert those to sample counts. The library defaults to
        // 44100, but Windows often runs audio at 48000. If we don't
        // correct it, the pitch will be wrong by a factor of 48000/44100
        // ≈ 1.088 (about 1.5 semitones off).
        //
        // setParameters(sampleRate, sequenceMs, seekWindowMs, overlapMs)
        // Passing 0 for the last three tells SoundTouch "use your own
        // tuned defaults" — they're chosen for music and work well.
        //
        // Wrapped in try/catch because some versions of the library
        // might not expose .stretch directly; if it fails, pitch
        // shifting still works, just at the library's default rate.
        try {
            this._st.stretch.setParameters(sampleRate, 0, 0, 0);
        } catch (_) {}

        // ── Defaults: no pitch change, no tempo change, no rate change ──
        // "pitch" controls the perceived pitch.
        // "tempo" controls the duration without affecting pitch.
        // "rate" changes both pitch and duration together (like a tape
        //   speeding up — used internally by SoundTouch).
        // All three at 1.0 = identity transform (passthrough).
        this._st.pitch = 1.0;
        this._st.tempo = 1.0;
        this._st.rate  = 1.0;

        // Cache the last pitch value to avoid re-sending it every block.
        // Comparing values is cheap; calling setPitch when nothing
        // changed is slightly less cheap. Minor optimization.
        this._lastPitch = 1.0;

        // ── Scratch buffers ──
        // These are used to convert between the browser's "planar"
        // audio layout (left channel in one array, right in another)
        // and SoundTouch's "interleaved" layout (L,R,L,R,L,R,...).
        //
        // Start small (256 samples) — they grow on demand in _ensure().
        // 256 is a safe default because most blocks are 128 samples;
        // the first allocation will need 128*2=256 floats, exactly
        // what we start with. Oversized buffers waste memory.
        this._inBuf  = new Float32Array(256 * 2);
        this._outBuf = new Float32Array(256 * 2);
    }

    /**
     * ── ENSURE BUFFER CAPACITY ──
     *
     * Grow the scratch buffers if the audio block is bigger than what
     * we currently have space for.
     *
     * Why "n * 2"?
     *   Each "frame" is one stereo sample = left + right = 2 floats.
     *   So n frames of audio need n*2 slots in the interleaved buffer.
     *
     * In practice the block size is almost always 128, so this runs
     * once at startup and never again. Kept here for safety in case
     * a future browser uses a different block size.
     */
    _ensure(n) {
        if (this._inBuf.length  < n * 2) this._inBuf  = new Float32Array(n * 2);
        if (this._outBuf.length < n * 2) this._outBuf = new Float32Array(n * 2);
    }

    /**
     * ── THE PROCESS METHOD ──
     *
     * Called by the audio system approximately every 2.9 ms (at 44.1 kHz
     * with 128-sample blocks). Each call must:
     *   1. Read the input audio.
     *   2. Do the DSP (pitch shifting).
     *   3. Write the output audio.
     *   4. Return true (meaning "I'm still alive, keep calling me").
     *
     * Returning false would stop the processor forever. We always
     * return true (see the end of the method).
     *
     * PARAMETERS:
     *   inputs:  an array of input channels. inputs[0] is the first
     *            connected source — an array where inputs[0][0] is
     *            the left channel (Float32Array) and inputs[0][1]
     *            is the right channel (if stereo).
     *   outputs: same shape, but empty arrays we fill with our result.
     *   params:  an object mapping parameter names to their values
     *            for this block. For k-rate params, params.pitch is
     *            an array with one element — but you can also treat
     *            it as a scalar via [0].
     */
    process(inputs, outputs, params) {
        // ── Safety check: is there anything connected? ──
        // If the source is disconnected (e.g., between tracks, or on
        // shutdown), inputs[0] will be an empty array. Returning true
        // here means "I'm ready if you reconnect" — the browser keeps
        // calling us, and we do nothing until audio comes back.
        const input = inputs[0];
        if (!input || input.length === 0) return true;

        // ── Unpack the input/output arrays for readability ──
        // The browser gives us audio in "planar" format:
        //   inL = [sample0_left, sample1_left, sample2_left, ...]
        //   inR = [sample0_right, sample1_right, ...]
        //
        // For mono sources, input[1] is undefined, so we mirror the
        // left channel to the right (inR = inL).
        const inL  = input[0];
        const inR  = input[1] || input[0];
        const outL = outputs[0][0];
        const outR = outputs[0][1] || outputs[0][0];
        const N    = outL.length;   // number of frames in this block (usually 128)

        // ── Read the current pitch parameter ──
        // params.pitch is an array with one element (k-rate parameter).
        // [0] extracts the single value.
        const p = params.pitch[0];

        // Only push the new value into SoundTouch when it actually
        // changed. Setting the same value would trigger a recompute
        // of internal window sizes for nothing. Minor perf win, but
        // it adds up over millions of blocks.
        if (p !== this._lastPitch) {
            this._st.pitch = p;
            this._lastPitch = p;
        }

        // Make sure the scratch buffers are big enough for this block.
        this._ensure(N);

        // ── STEP 1: interleave the input ──
        // SoundTouch expects "interleaved" stereo:
        //   [L0, R0, L1, R1, L2, R2, ...]
        // but the browser gives us separate arrays. This loop copies
        // sample by sample into the interleaved layout.
        const inBuf = this._inBuf;
        for (let i = 0; i < N; i++) {
            inBuf[i * 2]     = inL[i];   // left  goes to even index
            inBuf[i * 2 + 1] = inR[i];   // right goes to odd  index
        }

        // ── STEP 2: push the interleaved block into SoundTouch ──
        // putSamples(sourceArray, startPosition, numberOfFrames)
        // This copies N stereo frames (2N floats) from inBuf into
        // SoundTouch's internal input queue. Nothing is processed yet
        // — it's just buffered.
        this._st.inputBuffer.putSamples(inBuf, 0, N);

        // ── STEP 3: run the DSP ──
        // This is where the actual pitch-shifting math happens. It
        // consumes available input and produces output into SoundTouch's
        // internal output buffer. Depending on how much was buffered,
        // it may produce more or less than N frames of output.
        this._st.process();

        // ── STEP 4: how many output frames are ready? ──
        // On the first few blocks of a new track, SoundTouch needs to
        // fill its internal buffers before producing output. During
        // that warm-up window, frameCount will be less than N (or 0).
        // We handle that below by zero-filling.
        const available = this._st.outputBuffer.frameCount;
        const toPull    = Math.min(N, available);   // never request more than we have

        // ── STEP 5: zero the output buffer ──
        // We write into this scratch buffer, then copy to the actual
        // output channels. Zeroing first is important because when
        // toPull < N we only fill part of it; the rest must be silence,
        // not leftover garbage from a previous block.
        const outBuf = this._outBuf;
        for (let i = 0; i < N * 2; i++) outBuf[i] = 0;

        // ── STEP 6: pull the ready output frames ──
        // receiveSamples(destinationArray, numberOfFrames)
        // Copies frames out of SoundTouch's output queue into outBuf.
        // Only called if there's something to pull — calling it with
        // 0 would be a no-op but wasted work.
        if (toPull > 0) {
            this._st.outputBuffer.receiveSamples(outBuf, toPull);
        }

        // ── STEP 7: de-interleave into the output channels ──
        // Convert back from [L,R,L,R,...] to separate left and right
        // arrays. This is the reverse of Step 1.
        for (let i = 0; i < N; i++) {
            outL[i] = outBuf[i * 2];
            outR[i] = outBuf[i * 2 + 1];
        }

        // ── STEP 8: keep going ──
        // Return true so the browser calls us again for the next block.
        // Returning false would tell the browser "I'm done forever" —
        // useful for one-shot effects, not for a continuous shifter.
        return true;
    }
}

/**
 * ── REGISTER THE PROCESSOR ──
 *
 * This line MUST run when the worklet script is loaded (i.e., at the
 * top level, not inside a function). It does two things:
 *
 *   1. Saves the SoundTouchProcessor class under the name 'pitch-shifter'.
 *   2. Tells the browser "the processor is available under that name".
 *
 * Then, on the main thread, code can do:
 *
 *    const node = new AudioWorkletNode(audioContext, 'pitch-shifter');
 *
 * and the browser will create an instance of SoundTouchProcessor for
 * that node, connecting it to the audio graph.
 *
 * The name must be:
 *   • Unique within this AudioContext (two files can't both register
 *     'pitch-shifter').
 *   • The same string used in AudioWorkletNode(..., 'pitch-shifter').
 *
 * Everything AFTER this line but before the browser returns from
 * addModule() is used to initialize the processor; nothing else in
 * this file runs during normal operation.
 */
registerProcessor('pitch-shifter', SoundTouchProcessor);