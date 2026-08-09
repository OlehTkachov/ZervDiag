from search.search import search_files

for query in ["гидроаккумулятор", "Dana Spicer", "КС-55727"]:
    print("\n===", query, "===")
    results = search_files(query)
    print("Найдено:", len(results))

    for r in results[:5]:
        print(r.filename)
        print(r.filepath)
        print(r.snippet)
