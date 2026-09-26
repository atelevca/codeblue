# Запуск на macOS

**Статус: проверено на Apple Silicon (M3 Max).** Полный конвейер, коррекция терминов и
привязка спикеров проходят. Разработка по-прежнему идёт под Windows; всё macOS-специфичное
вынесено так, чтобы Windows-сборку не трогать: отдельный профиль запуска `mac`,
`HealthTech/run.sh` и условие в `HealthTech.csproj`.

## 1. Что поставить

```sh
# .NET 10 SDK
brew install --cask dotnet-sdk

# ffmpeg и ffprobe — конвейер вызывает их как внешние процессы
brew install ffmpeg

dotnet --version   # ожидается 10.x
ffprobe -version
```

Пути к ffmpeg берутся из конфига (`Audio:FfmpegPath`, `Audio:FfprobePath`), по умолчанию
ищутся в PATH. После `brew install` они там и будут. Если нет — задайте явно:

```sh
export Audio__FfmpegPath=/opt/homebrew/bin/ffmpeg
export Audio__FfprobePath=/opt/homebrew/bin/ffprobe
```

Если ffmpeg не запускается, приложение отвечает 503 с `FfmpegUnavailable`, а не падает.

## 2. Модели

Приложение **никогда не скачивает модели само**. Положите пять файлов в `models/`
в корне репозитория (каталог в `.gitignore`). Все, кроме GGUF, скачивает
`./download-models.sh` из корня репозитория (на Windows — из Git Bash); уже скачанные
файлы он пропускает. GGUF кладётся руками:

| Файл | Что это | Размер |
|---|---|---|
| `ggml-large-v3.bin` | Whisper large-v3 | ~3 ГБ |
| `pyannote-segmentation-3.0.onnx` | сегментация речи | ~6 МБ |
| `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` | эмбеддинги голосов (CAM++) | ~27 МБ |
| `silero_vad.onnx` | детектор речи | ~2 МБ |
| `qwen2.5-7b-instruct-q4_k_m.gguf` | LLM для коррекции и протокола | ~4,7 ГБ |

Отсутствие любого из первых четырёх **роняет старт** с указанием пути — это сделано
намеренно, валидаторами `ValidateOnStart`. Отсутствие GGUF роняет не старт, а первое
обращение к LLM (503 `LlmModelNotFound`).

Turbo-версию Whisper не берите: на румынском она зацикливается, поэтому в конфиге
стоит именно large-v3.

## 3. Запуск

```sh
HealthTech/run.sh
# то же самое: dotnet run --project HealthTech --launch-profile mac
```

Слушает `http://localhost:5089`, Swagger на `/swagger`. Профиль `mac` включает Whisper на
Metal (`WHISPER_GPU_DEVICE=0`) и выгружает все слои LLM на GPU (`Llm__GpuLayerCount=999`).
Профиль `http` на Mac тоже работает, но LLM в нём считается на CPU.

`HealthTech/run.bat` и профили `http`/`https` — для Windows: они выставляют
`GGML_VK_VISIBLE_DEVICES` под Vulkan, которого на macOS нет.

Проверка, что всё поднялось:

```sh
curl -s http://localhost:5089/profiles
# [{"key":"administrative",...},{"key":"financial",...},{"key":"medical",...}]
```

В логе при старте должны быть строки:

```
Схема прикладной базы применена из .../Data/schema.sql
Загружено профилей записей: 3 (administrative, financial, medical)
Loading Whisper model .../models/ggml-large-v3.bin
Whisper runtime: <имя рантайма>
Now listening on: http://localhost:5089
```

## 4. Что с ускорителями

Здесь главное отличие от Windows, и на него стоит потратить пять минут.

**Whisper.** Пакет `Whisper.net.Runtime` для `macos-arm64` уже содержит Metal-бэкенд
(`libggml-metal-whisper.dylib`), отдельный пакет Metal не нужен. `Whisper.net.Runtime.Vulkan`
подключается только не на macOS (условие `IsOSPlatform('OSX')` в `HealthTech.csproj`):
на Windows он по-прежнему пробуется первым. В логе при загрузке модели видно
`ggml_metal_device_init: GPU name: ...`.

**LLM.** `LLamaSharp.Backend.Cpu` уже содержит `libggml-metal.dylib` для `osx-arm64`.
В `SemanticKernel/appsettings.llm.json` стоит `"GpuLayerCount": 0` — это значение для
Windows-машины, его не трогаем. Профиль `mac` переопределяет его переменной
`Llm__GpuLayerCount=999` (все слои на GPU).

**Диаризация.** sherpa-onnx тянет нативные пакеты под `osx-arm64` и `osx-x64`
автоматически, делать ничего не нужно.

## 5. Переменные окружения

Любую настройку из `appsettings.json` можно переопределить переменной, заменив `:` на `__`:

```sh
export Whisper__UseGpu=false          # принудительно CPU
export Llm__GpuLayerCount=-1          # выгрузить LLM на Metal
export Audio__FfmpegPath=/opt/homebrew/bin/ffmpeg
export Uploads__MaxBytes=2147483648   # поднять лимит загрузки до 2 ГБ
```

`GGML_VK_VISIBLE_DEVICES` в профилях `http`/`https` нужна Windows-машине. Профиль `mac`
её не выставляет.

## 6. Где хранятся данные

Всё относительно корня репозитория, каталоги создаются сами:

```
assets/input/<jobId>/       загруженный файл
assets/processed/<jobId>/   WAV и его вариант 16 кГц моно
transcripts/<jobId>/        транскрипты, диалог, отчёт коррекции, протокол
data/healthtech.db          задания, врачи, привязки (Dapper)
data/workflow.db            состояние WorkflowCore
logs/healthtech-*.log       Serilog, хранится 14 файлов
```

Обе базы SQLite создаются при первом старте. Если нужно начать с чистого листа —
остановите приложение и удалите каталог `data/`.

## 7. Чего ожидать по времени

На двухминутной записи, Windows, Whisper на GPU (Vulkan), LLM на CPU:

| шаг | время |
|---|---|
| нормализация и подготовка | секунды |
| распознавание | 4–5 мин |
| диаризация | 15–40 с |
| коррекция терминов | ~5 мин |
| генерация протокола | 6–10 мин |

На той же записи, Apple M3 Max, профиль `mac` (Whisper и LLM на Metal):

| шаг | время |
|---|---|
| нормализация и подготовка | секунды |
| распознавание | ~16 с |
| диаризация | ~14 с |
| коррекция терминов | ~16 с |

## 8. Известные грабли

**Не запускайте несколько записей параллельно.** Два одновременных контекста LLM
поднимали процесс до 10,9 ГБ и приводили к тому, что система убивала процессы. Уборки
заданий нет, ограничения на число одновременных конвейеров тоже — это известное
ограничение прототипа.

**Каталоги заданий не чистятся.** `assets/input/<jobId>/` и `assets/processed/<jobId>/`
копятся после каждой загрузки. Удаляйте руками.

**Аутентификации нет.** Все эндпоинты открыты. Не выставляйте наружу.

**Язык распознавания зафиксирован румынским** (`Whisper:Language: ro`). Значение `auto`
определяет язык по каждому чанку вразнобой и начинает переводить румынскую речь в
русскую — поэтому оно и зафиксировано.

## 9. Если не заводится

| Симптом | Причина |
|---|---|
| Старт падает с путём к `.onnx` или `.bin` | нет модели в `models/`, см. раздел 2 |
| Старт падает на `не найден системный промпт` | не собрался `SemanticKernel`, промпты не скопировались в вывод |
| 503 `FfmpegUnavailable` | ffmpeg не в PATH, задайте `Audio__FfmpegPath` |
| 503 `LlmModelNotFound` | нет GGUF; всё остальное при этом работает |
| 422 при загрузке файла | файл не аудио или пустой — проверяется по содержимому через ffprobe, а не по расширению |
| Задание навсегда в `Running` | не должно случаться: при перезапуске такие помечаются `Failed`. Если случилось — это дефект, покажите лог |
