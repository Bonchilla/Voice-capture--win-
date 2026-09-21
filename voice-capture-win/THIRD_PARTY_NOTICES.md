# Сторонние компоненты — VoiceCapture 0.2.0

Личная поставка. Права на исходное Mac-приложение не выводятся из лицензий зависимостей; перед публичным распространением необходима отдельная проверка прав и полного состава поставки.

| Компонент | Версия / источник | Условия |
|---|---|---|
| NAudio | 2.2.1 | MIT; [лицензия](licenses/NAudio.txt) |
| sherpa-onnx | NuGet 1.13.8; upstream commit dc5583f49917e4c95f6e7d862bb378e4ed5e9076 | Apache-2.0; [лицензия](licenses/sherpa-onnx.txt) |
| ONNX Runtime | Нативная зависимость Windows x64 пакета sherpa-onnx 1.13.8 | MIT; [лицензия](licenses/ONNX-Runtime.txt), [сторонние уведомления upstream](licenses/ONNX-Runtime-third-party.txt) |
| .NET | Самодостаточная поставка .NET 10 | [Лицензия](licenses/Dotnet.txt), [уведомления](licenses/Dotnet-third-party.txt) |
| NVIDIA Parakeet TDT 0.6B v3 | Модель NVIDIA, скачивается отдельно | CC BY 4.0; [полный текст](licenses/Parakeet-CC-BY-4.0.txt) |

## Атрибуция модели и изменения формата

Модель **Parakeet TDT 0.6B v3** разработана NVIDIA: [исходная модель и карточка](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3). Условия — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Указание NVIDIA не означает одобрения или сопровождения этого приложения компанией NVIDIA.

Приложение использует не исходный NeMo checkpoint, а **ONNX-экспорт с INT8-квантизацией**, подготовленный сопровождающим sherpa-onnx: [csukuangfj / sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8), закреплённая ревизия **2bda32ec70b097a55adaa07d9a7173915b43cc78**. Преобразование и квантизация изменяют формат/точность весов и могут влиять на качество; приложение не дообучает модель. Веса не включены в архив приложения, скачивание выполняется пользователем из настроек. Эти атрибуция и условия применимы и к отдельно скачанным компонентам модели.

[Официальный sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), [пример Parakeet](https://github.com/k2-fsa/sherpa-onnx/blob/dc5583f49917e4c95f6e7d862bb378e4ed5e9076/python-api-examples/offline-nemo-parakeet-decode-file.py), [ONNX Runtime](https://github.com/microsoft/onnxruntime), [NAudio](https://github.com/naudio/NAudio), [.NET](https://github.com/dotnet/runtime).

Тексты ONNX Runtime получены из официального upstream; сторонние уведомления приведены как дополнительный набор upstream notices, а не утверждение, что все перечисленные компоненты используются приложением. Перед публичным распространением требуется сверить транзитивные нативные зависимости конкретной поставки.

Whisper.net и whisper.cpp больше не входят в исполняемые зависимости 0.2.0. Их прежние лицензии сохранены в репозитории для предыдущих выпусков, но новая публикация их не копирует.

OpenAI API — отдельный платный сервис. Его условия не заменяются лицензиями локальных библиотек. VC++ Runtime устанавливается отдельно из официального источника. GPU-драйверы не поставляются и не меняются приложением.
