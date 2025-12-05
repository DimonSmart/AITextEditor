# AI Text Editor

## Путь от запроса до правки
- MarkdownDocumentRepository парсит markdown в плоский список блоков с ID и PlainText.
- ChunkBuilder режет документ на чанки, а InMemoryVectorStore индексирует их для контекста.
- AiCommandPlanner ищет нужную главу (по совпадению заголовка) или фрагмент (по цитате в запросе) и передает подходящие блоки в LLM.
- FunctionCallingLlmEditor формирует промпт со списком допустимых команд (replace/insert_after/insert_before/remove) и просит модель вернуть JSON операций.
- ILlmClient по умолчанию — SemanticKernelLlmClient, который через Semantic Kernel + Ollama дергает LLM (модель по умолчанию `qwen3:latest`, эндпоинт из `OLLAMA_ENDPOINT`, по умолчанию `http://localhost:11434`). В тестах его можно завернуть в HttpClientVcr, чтобы повторные вызовы брались из кассет.
- DocumentEditor применяет операции к блокам, а затем MarkdownDocumentRepository сохраняет обновленный markdown.

## LLM и кеширование
- SemanticKernelLlmClient.CreateOllamaClient(modelId, endpoint, httpClient) подключает чат-модель Ollama через Semantic Kernel.
- HttpClientVcr (tests/AiTextEditor.Tests/Infrastructure/HttpClientVcr.cs) пишет/читает кассеты по SHA-256 хэшу содержимого запроса и URL, позволяя запускать тесты без повторных сетевых обращений к LLM.

## Запуск с реальным Ollama
- Поднимите Ollama (`ollama serve`) и скачайте модель `ollama pull qwen3:latest`.
- Переменные окружения (необязательные):
  - `OLLAMA_ENDPOINT` — адрес демона (по умолчанию `http://localhost:11434`).
  - `OLLAMA_MODEL` — id модели (по умолчанию `qwen3:latest`).
- Консольный пример использует SemanticKernelLlmClient + FunctionCallingLlmEditor и отправляет промпт прямо в Ollama.

## Тесты
- AiCommandPlannerTests проверяет, что запрос в нужную главу или с цитатой превращается в корректные EditOperation.
- HttpClientVcrTests гарантирует запись и повторное воспроизведение ответов по хэшу запроса.
