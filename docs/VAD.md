# Voice activity detection

Martlet finds where speech starts and ends with an energy detector and an
end-of-turn judge. Both ship in the desktop app.

## Always listening's end-of-turn judge

Always listening in the desktop uses an
energy detector (`EnergyVoiceActivityDetector` in Martlet.Audio) and, on top of
it, an end-of-turn judge (`EndOfTurnGate` in Martlet.Conversation). With
Companion › Listening › *Judge when I finish talking* on (the default), the
detector itself waits for the longer pause for unfinished speech
(`EndOfTurnGate.DetectorEndSilence`, at least 1.6 s). After 260 ms of silence
the gate asks Smart Turn v3.2 (`SmartTurnEngine` in Martlet.Sherpa, the
bundled `turn-detection\smart-turn-v3.2-cpu.onnx`) and ends the speech early
with `EnergyVoiceActivityDetector.EndSpeech()` when it hears a finished turn.
At the plain pause (Reply after) the gate ends it unless the judge said the
turn is unfinished. Off, missing or failed, the detector runs with the plain
pause exactly as before. Smart Turn runs through the ONNX Runtime that ships
with sherpa-onnx (1.28.2). See
[Voice latency](VOICE_LATENCY.md#the-end-of-turn-judge) and
[Conversation](CONVERSATION.md#hands-free-voice-activity-and-voice-id).

At the same 260 ms check point, Companion › Listening › *Start replies early*
(on by default, with Parakeet on this PC) starts the reply on the quick
transcript, held, and keeps it only when the detector ends the turn in that
same pause; the first frame of the user's own voice in the pause (never what
the speakers play, `EchoTimeline.Speakers`, and never what *Hear what this PC
plays* hears) lets it go. Without a judge, the quick transcript still starts
at 260 ms for it (only when a reply may start early with the Thinking model in
use), and speech-to-text reuses it when the plain pause ends the turn there. See
[Voice latency](VOICE_LATENCY.md#starting-replies-early).
