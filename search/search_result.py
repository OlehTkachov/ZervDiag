from dataclasses import dataclass


@dataclass
class SearchResult:
    file_id: int
    filename: str
    extension: str
    filepath: str
    snippet: str
    is_cloud: bool