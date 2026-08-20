SUPPORTED_LANGUAGES = {
    "ru": "Русский",
    "uk": "Українська",
    "en": "English",
}

DEFAULT_LANGUAGE = "ru"


TRANSLATIONS = {
    "ru": {
        "app.title": "ZervDiag",
        "common.close": "Закрыть",
        "common.cancel": "Отмена",
        "common.save": "Сохранить",
        "common.refresh": "Обновить",
        "nav.search": "Поиск",
        "nav.index": "Индексация",
        "nav.scan_recognition": "Распознать сканы ({count})",
        "nav.stop_recognition": "Остановить распознавание",
        "nav.choose_folder": "Выбрать папку",
        "nav.duplicates": "Клоны",
        "nav.settings": "Настройки",
        "nav.database_status": "Статистика базы",
        "search.folder": "Папка документации:",
        "search.no_folder": "Папка не выбрана",
        "search.find": "Найти",
        "search.placeholder": "КС 55727, ОНК160, E10, Terex AC35L...",
        "search.file": "Файл",
        "search.type": "Тип",
        "search.status": "Статус",
        "search.snippet": "Фрагмент",
        "search.path": "Полный путь",
        "search.ready": "Готово",
        "search.running": "Поиск «{query}» выполняется...",
        "search.next": "Текущий поиск ещё выполняется. Следующий запрос: {query}",
        "search.not_found_title": "Документ не найден",
        "search.not_found_text": "По запросу «{query}» документы в локальной базе не найдены.",
        "search.not_found_info": "Можно изменить запрос или выполнить поиск в интернете.",
        "search.edit_query": "Изменить запрос",
        "search.web": "Искать в интернете",
        "scan.tooltip": "Извлечь текст из отсканированных PDF и изображений, чтобы они участвовали в поиске.",
        "language.title": "Язык интерфейса",
        "language.prompt": "Выберите язык / Оберіть мову / Choose language",
    },
    "uk": {
        "app.title": "ZervDiag",
        "common.close": "Закрити",
        "common.cancel": "Скасувати",
        "common.save": "Зберегти",
        "common.refresh": "Оновити",
        "nav.search": "Пошук",
        "nav.index": "Індексація",
        "nav.scan_recognition": "Розпізнати скани ({count})",
        "nav.stop_recognition": "Зупинити розпізнавання",
        "nav.choose_folder": "Вибрати папку",
        "nav.duplicates": "Клони",
        "nav.settings": "Налаштування",
        "nav.database_status": "Статистика бази",
        "search.folder": "Папка документації:",
        "search.no_folder": "Папку не вибрано",
        "search.find": "Знайти",
        "search.placeholder": "КС 55727, ОНК160, E10, Terex AC35L...",
        "search.file": "Файл",
        "search.type": "Тип",
        "search.status": "Статус",
        "search.snippet": "Фрагмент",
        "search.path": "Повний шлях",
        "search.ready": "Готово",
        "search.running": "Пошук «{query}» виконується...",
        "search.next": "Поточний пошук ще виконується. Наступний запит: {query}",
        "search.not_found_title": "Документ не знайдено",
        "search.not_found_text": "За запитом «{query}» документи в локальній базі не знайдено.",
        "search.not_found_info": "Можна змінити запит або виконати пошук в інтернеті.",
        "search.edit_query": "Змінити запит",
        "search.web": "Шукати в інтернеті",
        "scan.tooltip": "Витягти текст зі сканованих PDF та зображень, щоб вони брали участь у пошуку.",
        "language.title": "Мова інтерфейсу",
        "language.prompt": "Выберите язык / Оберіть мову / Choose language",
    },
    "en": {
        "app.title": "ZervDiag",
        "common.close": "Close",
        "common.cancel": "Cancel",
        "common.save": "Save",
        "common.refresh": "Refresh",
        "nav.search": "Search",
        "nav.index": "Indexing",
        "nav.scan_recognition": "Recognize scans ({count})",
        "nav.stop_recognition": "Stop recognition",
        "nav.choose_folder": "Choose folder",
        "nav.duplicates": "Duplicates",
        "nav.settings": "Settings",
        "nav.database_status": "Database status",
        "search.folder": "Documentation folder:",
        "search.no_folder": "No folder selected",
        "search.find": "Find",
        "search.placeholder": "KS 55727, ONK160, E10, Terex AC35L...",
        "search.file": "File",
        "search.type": "Type",
        "search.status": "Status",
        "search.snippet": "Snippet",
        "search.path": "Full path",
        "search.ready": "Ready",
        "search.running": "Search “{query}” is running...",
        "search.next": "The current search is still running. Next query: {query}",
        "search.not_found_title": "Document not found",
        "search.not_found_text": "No documents matching “{query}” were found in the local database.",
        "search.not_found_info": "You can change the query or search the internet.",
        "search.edit_query": "Edit query",
        "search.web": "Search the internet",
        "scan.tooltip": "Extract text from scanned PDFs and images so they can participate in search.",
        "language.title": "Interface language",
        "language.prompt": "Выберите язык / Оберіть мову / Choose language",
    },
}


def normalize_language(language):
    language = (language or "").strip().lower().replace("_", "-")

    if language in SUPPORTED_LANGUAGES:
        return language

    if language.startswith("uk") or language.startswith("ua"):
        return "uk"

    if language.startswith("en"):
        return "en"

    if language.startswith("ru"):
        return "ru"

    return DEFAULT_LANGUAGE


def tr(key, language=DEFAULT_LANGUAGE, **values):
    language = normalize_language(language)

    text = TRANSLATIONS.get(language, {}).get(key)

    if text is None:
        text = TRANSLATIONS[DEFAULT_LANGUAGE].get(key, key)

    if values:
        try:
            return text.format(**values)
        except (KeyError, ValueError):
            return text

    return text
