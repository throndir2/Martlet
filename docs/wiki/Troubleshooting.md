# Troubleshooting

Start on **Home**. It lists what needs attention and opens the page that can fix it.

## Martlet cannot reply

Check **Companion › Thinking** and **Devices** for missing setup, removed key, retired model, Ollama not running, missing Ollama model, host not answering or Docker Desktop stopped.

## Local Ollama problems

Use **Companion › Thinking › This PC › Test model**. It checks Ollama, model download, load, response and common memory errors. Choose a smaller model if GPU memory is overfilled.

## Character problems

Check **Show character**, WebView2 runtime, **Companion › Character** status and Diagnostics `avatar-renderer.log`. Use **Settings › Tools › Prerequisites** for WebView2.

## Audio problems

Check Windows microphone privacy, selected devices, Listening route and Audio setup. For Windows speech, use **Martlet prerequisites** to check installed speech features.

## Docker/WSL problems

Use **Prerequisites (check / install)**. Martlet checks WSL 2, Virtual Machine Platform, Docker Desktop, pending restart and hardware virtualization. If firmware virtualization is off, Martlet can only guide you.

## Diagnostics

Open **Diagnostics** to filter logs, copy lines, open this PC's logs folder or **Save logs to share**. Review before sharing: logs can include local paths, computer names and provider error text.

## Crash or unexpected close

Home shows unexpected errors or a previous unexpected close. Open Diagnostics or the logs folder. Native crashes may be summarized from Windows Event Viewer on next launch.

More detail: [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Voice latency](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_LATENCY.md).
