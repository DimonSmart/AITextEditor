# Потоковый навигационный агент

## Зачем нужен потоковый режим
AITextEditor редактирует большие Markdown-документы по текстовым командам. Модель не держит весь текст в контексте: она читает документ небольшими порциями, находит нужное место и возвращает только релевантные фрагменты. Это удешевляет работу с длинными книгами, уменьшает шум и исключает случайные правки лишнего текста.

Основные принципы:
- читать документ порциями, а не целиком;
- адресовать элементы семантическим указателем (`SemanticPointer.ToCompactString()` вида `id:label`);
- изменять через доменные операции, а не свободным переписыванием Markdown;
- длинные циклы навигации выносить в отдельного подагента;
- в ответах подагента возвращать минимум: pointer + markdown + reason/progress.

## Архитектура и роли
1. **Основной агент (Command Agent)** получает команду пользователя, выбирает тип курсора и формирует задачу.
2. **Подагент потоковой навигации (CursorAgent)** читает порции из уже созданного курсора, на каждый батч возвращает JSON-команду, а в конце финализатор выбирает итоговый результат из собранного evidence.

Типовой ход работы:
1. Пользователь пишет команду, например: "Найди упоминание профессора Звездочкина (исключая заголовки)".
2. Основной агент создает курсор: `cursor-create_keyword_cursor`, `cursor-create_full_scan_cursor` или `cursor-create_filtered_cursor`.
3. Основной агент вызывает `cursor_agent-run_cursor_agent` с именем курсора и задачей.
4. Runtime читает порции, вызывает модель на каждый батч, собирает evidence и завершает работу финализатором.
5. Основной агент использует `CursorAgentResult` для ответа или последующей правки.

## Сообщения и память
Каждый шаг использует новую историю чата (нет накопления между шагами). В модель передаются три JSON-сообщения:

- **task**:
  - `type: "task"`
  - `orderingGuaranteed: true`
  - `goal`: текст задачи
  - `context`: опциональный контекст
  - `maxEvidenceCount`: опциональный лимит, подсказка для ранней остановки
- **snapshot**:
  - `type: "snapshot"`
  - `evidenceCount`: число уже найденных элементов
  - `recentEvidencePointers`: последние указатели (обрезаются по `SnapshotEvidenceLimit`)
- **batch**:
  - `firstBatch`
  - `hasMoreBatches`
  - `items`: `pointer`, `itemType`, `markdown`

Поле `needMoreContext` парсится, но сейчас не запускает дополнительный поток данных (резерв на будущее).

## Команда подагента (JSON)
```json
{
  "action": "continue|stop",
  "batchFound": true|false,
  "newEvidence": [
    { "pointer": "...", "excerpt": "...", "reason": "..." }
  ],
  "progress": "...",
  "needMoreContext": true|false
}
```

Правила:
- `pointer` должен совпадать с `batch.items[].pointer`.
- `excerpt` должен быть дословным `markdown` элемента из текущей порции; рантайм нормализует evidence до полного markdown.
- `reason` - одно локальное объяснение без ссылок на глобальный порядок.
- `progress` используется как краткое резюме батча и может попасть в `CursorAgentResult.Summary`.
- `action="stop"` завершает цикл; иначе сканирование продолжается до исчерпания курсора или лимита шагов.
- `maxEvidenceCount` - подсказка для ранней остановки, не является жестким ограничением.

## Курсоры и функции

### cursor (плагин)
- `create_filtered_cursor(filterDescription, maxElements?, maxBytes?, startAfterPointer?, includeHeadings?)`
  - создает именованный курсор.
  - `filterDescription` сейчас хранится как описание и не фильтрует поток.
  - `maxElements`/`maxBytes` ограничиваются `CursorAgentLimits.MaxElements/MaxBytes`.
- `create_keyword_cursor(keywords, includeHeadings?)`
  - фильтрует элементы по ключевым словам (RU/EN стемминг, логическое OR).
- `create_full_scan_cursor(includeHeadings?)`
  - отдает все элементы по порядку.
- `read_cursor_batch(cursorName)`
  - возвращает JSON с `items` (`SemanticPointer`, `Markdown`, `Type`), `HasMore`, `nextAfterPointer`, `MaxElements`, `MaxBytes`.

### cursor_agent (плагин)
- `run_cursor_agent(cursorName, taskDescription, startAfterPointer?, context?, maxEvidenceCount?)`
  - использует существующий курсор; позиция задается при создании курсора.
  - возвращает `CursorAgentResult`.

### editor (плагин)
- `create_targets(label, pointers[])` - формирует TargetSet и возвращает `targetSetId`, `targets` (pointer + excerpt), `invalidPointers`, `warnings`.
- `get_default_document_id`, `show_user_message`.

## Результат CursorAgent
`CursorAgentResult` содержит:
- `Success` - найден ли результат.
- `Summary` - краткое резюме/причина остановки.
- `SemanticPointerFrom` - выбранный семантический указатель (строка из курсора).
- `Excerpt` - markdown элемента.
- `WhyThis` - объяснение выбора финализатора.
- `Evidence` - список найденных элементов (уникальные pointers, ограничены `DefaultMaxFound`).
- `NextAfterPointer` - указатель для продолжения сканирования.
- `CursorComplete` - признак конца курсора.

## Лимиты и поведение
- шаги: `DefaultMaxSteps` = 128, верхняя граница `MaxStepsLimit` = 512.
- порции: `MaxElements` = 3, `MaxBytes` = 4096.
- evidence: `DefaultMaxFound` = 20, `SnapshotEvidenceLimit` = 5.
- длина полей: `MaxSummaryLength` = 500, `MaxExcerptLength` = 1000.
- курсор хранит состояние и продолжает с последнего прочитанного элемента; evidence и summary живут только в рамках одного запуска `run_cursor_agent`.

## Семантический указатель
`SemanticPointer` состоит из числового `Id` и метки `Label` (например, `1.2.p3`). В курсорах и в параметрах инструментов используется строковый формат `id:label` из `SemanticPointer.ToCompactString()`.

## Минимальный пример
```text
1. cursor-create_keyword_cursor(["звездочкин"], includeHeadings=false)
2. cursor_agent-run_cursor_agent("kwd_cursor_0", "Найди упоминания профессора Звездочкина", context: "исключай заголовки", maxEvidenceCount: 3)
3. Использовать `SemanticPointerFrom`/`Excerpt` из результата и при необходимости `NextAfterPointer` для продолжения.
```
