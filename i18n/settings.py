from i18n.catalog import (
    DEFAULT_LANGUAGE,
    SUPPORTED_LANGUAGES,
    normalize_language,
    tr,
)


SETTINGS_KEY = "language"


def get_language(settings):
    """Читает сохранённый язык интерфейса из QSettings-подобного объекта."""
    return normalize_language(
        settings.value(
            SETTINGS_KEY,
            DEFAULT_LANGUAGE,
        )
    )


def set_language(settings, language):
    """Сохраняет язык и возвращает нормализованный код ru/uk/en."""
    language = normalize_language(language)
    settings.setValue(
        SETTINGS_KEY,
        language,
    )
    return language


def has_language(settings):
    return settings.contains(
        SETTINGS_KEY
    )
