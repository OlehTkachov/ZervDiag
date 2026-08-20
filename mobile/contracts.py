from dataclasses import asdict, dataclass


API_VERSION = "v1"


@dataclass(frozen=True)
class MobileSearchRequest:
    query: str
    limit: int = 50

    def to_dict(self):
        return asdict(self)


@dataclass(frozen=True)
class MobileSearchItem:
    file_id: int
    filename: str
    extension: str
    snippet: str
    filepath: str
    is_cloud: bool

    def to_dict(self):
        return asdict(self)


@dataclass(frozen=True)
class MobileSearchResponse:
    query: str
    total: int
    items: tuple[MobileSearchItem, ...]

    def to_dict(self):
        return {
            "query": self.query,
            "total": self.total,
            "items": [
                item.to_dict()
                for item in self.items
            ],
        }


@dataclass(frozen=True)
class MobileAskRequest:
    question: str
    language: str = "ru"

    def to_dict(self):
        return asdict(self)


@dataclass(frozen=True)
class MobileSource:
    source_id: str
    filename: str
    filepath: str
    excerpt: str

    def to_dict(self):
        return asdict(self)


@dataclass(frozen=True)
class MobileAskResponse:
    answer: str
    sources: tuple[MobileSource, ...]
    local_only: bool = True

    def to_dict(self):
        return {
            "answer": self.answer,
            "sources": [
                source.to_dict()
                for source in self.sources
            ],
            "local_only": self.local_only,
        }
