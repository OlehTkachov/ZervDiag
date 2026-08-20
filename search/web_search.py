import webbrowser
from urllib.parse import quote_plus


DEFAULT_WEB_SEARCH_TEMPLATE = (
    "https://www.google.com/search?q={query}"
)


def build_web_search_url(query):
    """
    Формирует URL поискового запроса.

    Никаких сетевых запросов ZervDiag сам не выполняет:
    URL открывается обычным системным браузером.
    """
    query = (query or "").strip()

    if not query:
        return ""

    return DEFAULT_WEB_SEARCH_TEMPLATE.format(
        query=quote_plus(query)
    )


def open_web_search(query):
    """
    Открывает поиск в системном браузере.

    Возвращает True/False по результату webbrowser.open().
    """
    url = build_web_search_url(query)

    if not url:
        return False

    return bool(
        webbrowser.open(
            url,
            new=2,
        )
    )
