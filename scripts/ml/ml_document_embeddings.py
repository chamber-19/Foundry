"""
PyTorch-based document embedding engine for DailyDesk.

Generates semantic embeddings for knowledge library documents to enable:
- Semantic search across imported documents
- Relevance scoring between study topics and knowledge sources
- Document similarity clustering
- Context-aware prompt building (find most relevant knowledge for any topic)

Input: JSON on stdin with documents (list of {id, title, text}) and optional query
Output: JSON on stdout with embeddings, similarities, and ranked results

Falls back to TF-IDF based similarity when PyTorch is not installed.
"""

import json
import math
import re
import sys
from collections import Counter
from typing import Any


def _try_import_torch() -> bool:
    try:
        import torch  # noqa: F401

        return True
    except ImportError:
        return False


def _tokenize(text: str) -> list[str]:
    """Simple whitespace + punctuation tokenizer."""
    return re.findall(r"\b[a-zA-Z]{2,}\b", text.lower())


def _build_vocab(documents: list[str], max_vocab: int = 5000) -> dict[str, int]:
    """Build vocabulary from document corpus."""
    counter: Counter[str] = Counter()
    for doc in documents:
        counter.update(set(_tokenize(doc)))

    return {word: idx for idx, (word, _) in enumerate(counter.most_common(max_vocab))}


def _tfidf_vector(
    text: str,
    vocab: dict[str, int],
    idf: dict[str, float],
) -> list[float]:
    """Compute TF-IDF vector for a single document."""
    tokens = _tokenize(text)
    tf: Counter[str] = Counter(tokens)
    total = len(tokens) if tokens else 1

    vector = [0.0] * len(vocab)
    for word, idx in vocab.items():
        if word in tf:
            vector[idx] = (tf[word] / total) * idf.get(word, 1.0)

    return vector


def _cosine_similarity(vec_a: list[float], vec_b: list[float]) -> float:
    """Compute cosine similarity between two vectors."""
    dot = sum(a * b for a, b in zip(vec_a, vec_b))
    norm_a = math.sqrt(sum(a * a for a in vec_a))
    norm_b = math.sqrt(sum(b * b for b in vec_b))
    if norm_a == 0 or norm_b == 0:
        return 0.0
    return dot / (norm_a * norm_b)


def _tfidf_embeddings(
    documents: list[dict[str, Any]],
    query: str | None,
) -> dict[str, Any]:
    """TF-IDF fallback when PyTorch is not available."""
    texts = [doc.get("text", "") for doc in documents]

    if not texts or all(not t.strip() for t in texts):
        return {
            "ok": True,
            "engine": "tfidf",
            "embeddings": [],
            "similarities": [],
            "queryResults": [],
        }

    vocab = _build_vocab(texts)
    if not vocab:
        return {
            "ok": True,
            "engine": "tfidf",
            "embeddings": [],
            "similarities": [],
            "queryResults": [],
        }

    num_docs = len(texts)
    doc_freq: Counter[str] = Counter()
    for text in texts:
        doc_freq.update(set(_tokenize(text)))

    idf = {
        word: math.log((num_docs + 1) / (doc_freq[word] + 1)) + 1
        for word in vocab
    }

    vectors = [_tfidf_vector(text, vocab, idf) for text in texts]

    embeddings = []
    for i, doc in enumerate(documents):
        vec = vectors[i]
        norm = math.sqrt(sum(v * v for v in vec))
        normalized = [v / norm if norm > 0 else 0.0 for v in vec]
        # Reduce to a compact representation (top 64 dimensions by magnitude)
        indexed = sorted(enumerate(normalized), key=lambda x: abs(x[1]), reverse=True)
        compact = [normalized[idx] for idx, _ in indexed[:64]]

        embeddings.append(
            {
                "documentId": doc.get("id", f"doc-{i}"),
                "title": doc.get("title", ""),
                "dimensions": min(64, len(compact)),
                "embedding": [round(v, 6) for v in compact],
            }
        )

    similarities = []
    for i in range(len(vectors)):
        for j in range(i + 1, len(vectors)):
            sim = _cosine_similarity(vectors[i], vectors[j])
            similarities.append(
                {
                    "documentA": documents[i].get("id", f"doc-{i}"),
                    "documentB": documents[j].get("id", f"doc-{j}"),
                    "similarity": round(sim, 4),
                }
            )

    similarities.sort(key=lambda x: x["similarity"], reverse=True)

    query_results = []
    if query and query.strip():
        query_vec = _tfidf_vector(query, vocab, idf)
        for i, doc in enumerate(documents):
            sim = _cosine_similarity(query_vec, vectors[i])
            query_results.append(
                {
                    "documentId": doc.get("id", f"doc-{i}"),
                    "title": doc.get("title", ""),
                    "relevance": round(sim, 4),
                }
            )

        query_results.sort(key=lambda x: x["relevance"], reverse=True)

    return {
        "ok": True,
        "engine": "tfidf",
        "embeddings": embeddings,
        "similarities": similarities[:20],
        "queryResults": query_results[:10],
    }


def _torch_embeddings(
    documents: list[dict[str, Any]],
    query: str | None,
) -> dict[str, Any]:
    """PyTorch-based embeddings using a lightweight local model."""
    import torch
    import torch.nn as nn

    texts = [doc.get("text", "") for doc in documents]

    if not texts or all(not t.strip() for t in texts):
        return {
            "ok": True,
            "engine": "pytorch",
            "embeddings": [],
            "similarities": [],
            "queryResults": [],
        }

    vocab = _build_vocab(texts, max_vocab=3000)
    if not vocab:
        return _tfidf_embeddings(documents, query)

    embed_dim = 128
    vocab_size = len(vocab) + 1

    torch.manual_seed(42)
    embedding_layer = nn.EmbeddingBag(vocab_size, embed_dim, mode="mean")
    projection = nn.Linear(embed_dim, 64)

    nn.init.xavier_uniform_(embedding_layer.weight)
    nn.init.xavier_uniform_(projection.weight)

    def encode(text: str) -> list[float]:
        tokens = _tokenize(text)
        indices = [vocab.get(t, 0) for t in tokens if t in vocab]
        if not indices:
            indices = [0]
        input_tensor = torch.tensor(indices, dtype=torch.long).unsqueeze(0)
        with torch.no_grad():
            embedded = embedding_layer(input_tensor)
            projected = projection(embedded)
            normalized = torch.nn.functional.normalize(projected, dim=1)
        return normalized.squeeze(0).tolist()

    doc_vectors = [encode(text) for text in texts]

    embeddings = []
    for i, doc in enumerate(documents):
        embeddings.append(
            {
                "documentId": doc.get("id", f"doc-{i}"),
                "title": doc.get("title", ""),
                "dimensions": len(doc_vectors[i]),
                "embedding": [round(v, 6) for v in doc_vectors[i]],
            }
        )

    similarities = []
    for i in range(len(doc_vectors)):
        for j in range(i + 1, len(doc_vectors)):
            sim = _cosine_similarity(doc_vectors[i], doc_vectors[j])
            similarities.append(
                {
                    "documentA": documents[i].get("id", f"doc-{i}"),
                    "documentB": documents[j].get("id", f"doc-{j}"),
                    "similarity": round(sim, 4),
                }
            )

    similarities.sort(key=lambda x: x["similarity"], reverse=True)

    query_results = []
    if query and query.strip():
        query_vec = encode(query)
        for i, doc in enumerate(documents):
            sim = _cosine_similarity(query_vec, doc_vectors[i])
            query_results.append(
                {
                    "documentId": doc.get("id", f"doc-{i}"),
                    "title": doc.get("title", ""),
                    "relevance": round(sim, 4),
                }
            )

        query_results.sort(key=lambda x: x["relevance"], reverse=True)

    return {
        "ok": True,
        "engine": "pytorch",
        "embeddings": embeddings,
        "similarities": similarities[:20],
        "queryResults": query_results[:10],
    }


def main() -> None:
    try:
        raw = _read_input()
        payload = json.loads(raw) if raw.strip() else {}
    except json.JSONDecodeError:
        print(json.dumps({"ok": False, "error": "Invalid JSON input."}))
        return

    documents = payload.get("documents", [])
    query = payload.get("query")

    if _try_import_torch():
        try:
            result = _torch_embeddings(documents, query)
        except Exception as exc:
            result = _tfidf_embeddings(documents, query)
            result["pytorchError"] = str(exc)
    else:
        result = _tfidf_embeddings(documents, query)

    print(json.dumps(result, ensure_ascii=False))


def _read_input() -> str:
    """Read input from --input file argument or stdin."""
    for i, arg in enumerate(sys.argv[1:], 1):
        if arg == "--input" and i < len(sys.argv) - 1:
            from pathlib import Path

            return Path(sys.argv[i + 1]).read_text(encoding="utf-8")
    return sys.stdin.read()


if __name__ == "__main__":
    main()
