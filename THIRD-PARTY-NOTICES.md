# Third-party notices

FlowType bundles the following components. Each is distributed under its own
license, reproduced or linked below. FlowType itself is MIT licensed — see
[LICENSE](LICENSE).

| Component | License | Source |
| --------- | ------- | ------ |
| Whisper.net | MIT | https://github.com/sandrohanea/whisper.net |
| whisper.cpp | MIT | https://github.com/ggerganov/whisper.cpp |
| OpenAI Whisper models | MIT | https://github.com/openai/whisper |
| LLamaSharp | MIT | https://github.com/SciSharp/LLamaSharp |
| llama.cpp / ggml | MIT | https://github.com/ggml-org/llama.cpp |
| Qwen3 and Qwen2.5 models (optional, downloaded on request) | Apache-2.0 | https://huggingface.co/Qwen |
| NAudio | MIT | https://github.com/naudio/NAudio |
| .NET runtime & WPF | MIT | https://github.com/dotnet/runtime |
| Microsoft Visual C++ Runtime (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, `vcomp140.dll`) | Microsoft Distributable Code — redistributable with the application | https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |
| Satoshi typeface | Fontshare Free License | https://www.fontshare.com/fonts/satoshi |
| Clash Display typeface | Fontshare Free License | https://www.fontshare.com/fonts/clash-display |

AI models are **not** bundled with the application — they are downloaded from
Hugging Face at the user's request and stored under `%APPDATA%\FlowType\models`
(speech) and `%APPDATA%\FlowType\llm` (the optional on-device language model,
checked against a pinned SHA-256 before use).

FlowType is inspired by [Wispr Flow](https://wisprflow.ai) but is an
independent project with no affiliation to, or code from, that product.
