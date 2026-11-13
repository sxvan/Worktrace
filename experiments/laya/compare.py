"""Compare the app's regex secret detector with the Laya decision model, line by line.

Each case is one line of text as it could appear on screen, labelled secret / not secret.
Fake secrets are assembled at run time so this file contains no credential-looking strings.

Usage:  .venv\\Scripts\\python.exe compare.py [--verbose]
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

os.environ.setdefault("USE_TF", "0")

REPO = Path(__file__).resolve().parents[2]
APP = REPO / "src/Capture.App/bin/Debug/net10.0-windows10.0.22621.0/win-x64/TimeCapture.exe"
VERBOSE = "--verbose" in sys.argv

B = "a1B2c3D4e5" * 4  # body for made-up tokens


def cases() -> list[tuple[str, bool, str]]:
    """(text, is_secret, group)"""
    secret_clean = [
        "export ANTHROPIC_API_KEY=sk-" + "ant-api03-" + B,
        "git remote set-url origin https://gh" + "p_" + B + "@github.com/acme/app.git",
        "aws_access_key_id = AKIA" + "IOSFODNN7EXAMPLE",
        "aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCY" + "EXAMPLEKEY",
        "password = hunter2",
        '"client_secret": "Zx9Kq2Lm8Pw4Tr6Y",',
        "DATABASE_URL=postgres://admin:s3cretpw@db.internal:5432/app",
        "Authorization: Bearer abcdefghijklmnop1234567890QRSTUV",
        "-----BEGIN OPENSSH PRIVATE KEY-----",
        "STRIPE_SECRET_KEY=sk_" + "live_" + B[:24],
        '"password": "Tr0ub4dor&3"',
        "PGPASSWORD=Sommer2024! psql -h db.internal -U app",
        "export DB_PASS=Sommer2024!",
        "mysql -u root -pS3cretPass prod_db",
        'curl -H "X-Api-Key: 9f8e7d6c5b4a39281706f5e4d3c2b1a0" https://api.example.com/v1/users',
        'token = "a8f5f167f44f4964e6c998dee827110c"',
        "secretKey: 'xK9#mP2$vL5nQ8wR'",
        "the wifi password is correct horse battery staple",
        "login: admin / Sommer2024!",
        "my bank pin is 4921",
        "SLACK_BOT_TOKEN=xox" + "b-123456789012-" + B[:24],
        "HF_TOKEN=hf" + "_" + B[:34],
        "JWT: eyJ" + "hbGciOiJIUzI1NiJ9" + ".eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0" + ".abcDEF123456xyz",
        "Use this one-time code to sign in: Zx9Kq2Lm8Pw4Tr6YbN3cV7",
        "net use Z: \\\\fileserver\\share /user:corp\\svc_backup Winter!2026",
    ]
    secret_ocr = [  # the same kind of line as text recognition really returned it
        "password hunter2",
        '"client secret : " " Zx9Kq2Lm8Pw4Tr6Y" ,',
        '"client secret "Zx9Kq2Lm8Pw4Tr6Y",',
        "= AKIAIOSFODNN7EXAPIPLE",
        "DATABASE_URL=postgres : / / admin : s3cretpw@db.internal : 5432/ app",
        "DATABASE_URL=postgres : / / admin : s 3cretpQdb. internal : 5432/ app",
        "export ANTHROPIC API KEY=sk-" + "ant-apiæ-" + B,
        "PGPASSWORD Sommer2024! psql -h db. internal -U app",
        "token = \"a8f5f167f44f4964e6c998dee82711Oc\"",
        "STRIPE SECRET KEY=sk" + " live " + B[:24],
    ]
    harmless = [
        "public async Task<int> CountTokensAsync(string text, CancellationToken token)",
        "    var result = await client.SendAsync(request, cancellationToken);",
        "commit 3f786850e387550fdab836ed7e6dc881de23001b (HEAD -> main)",
        "The quick brown fox jumps over the lazy dog 0123456789.",
        "maxTokens: number; usePassword: boolean; // settings",
        "password = null",
        "public string Password { get; set; }",
        "const token = await getToken();",
        "Forgot your password? Reset it here.",
        "Enter your API key in the settings page to continue.",
        'import { SecretClient } from "@azure/keyvault-secrets";',
        "sha256:3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a",
        "https://github.com/acme/app/commit/3f786850e387550fdab836ed7e6dc881de23001b",
        "id: 550e8400-e29b-41d4-a716-446655440000",
        "Order 20261113-000123 has shipped and arrives on Friday.",
        "npm install --save-dev @types/node@22.10.1 typescript@5.7.2",
        "C:\\Users\\dev\\AppData\\Local\\Temp\\tmp8F3A.tmp",
        "401 Unauthorized: Bearer token required",
        "id_token: string | undefined",
        "secret santa list for the office party",
        "Change your password every 90 days.",
        "password_hash = bcrypt.hashpw(pw, salt)",
        'git commit -m "rotate api keys and update docs"',
        "TODO: remove hardcoded credentials before the release",
        "ACCESS_TOKEN_EXPIRE_MINUTES = 30",
        "token_type: bearer",
        "SELECT id, email FROM users WHERE password IS NOT NULL;",
        "server listening on http://localhost:8080",
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk",
        "Meeting moved to 14:30, room Matterhorn, bring the Q3 numbers.",
        "def rotate_secret(name: str, ttl: int = 3600) -> None:",
        "<input type=\"password\" name=\"password\" autocomplete=\"current-password\">",
        "error TS2345: Argument of type 'string' is not assignable to parameter of type 'number'.",
        "Timesheet entry: 2.5 h code review for project Helvetia, ticket HEL-4821",
        "docker run -e POSTGRES_PASSWORD_FILE=/run/secrets/db_password postgres:17",
    ]
    return ([(t, True, "secret, clean") for t in secret_clean]
            + [(t, True, "secret, OCR-damaged") for t in secret_ocr]
            + [(t, False, "harmless") for t in harmless])


def run_regex(texts: list[str]) -> list[bool]:
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
        f.write("\n".join(texts) + "\n")
        path = f.name
    try:
        out = subprocess.run([str(APP), "--detect-lines", path], capture_output=True, text=True, check=True).stdout
    finally:
        os.unlink(path)
    flags = [line.strip() == "1" for line in out.splitlines() if line.strip() in ("0", "1")]
    assert len(flags) == len(texts), (len(flags), len(texts))
    return flags


QUESTIONS = {
    "yes/no": {"q": {
        "type": "noul",
        "instructions": "Does this line of text contain an actual secret credential value, such as a password, "
                        "API key, access token or private key, written out in it?",
        "criteria": {
            "false": "No actual secret value appears; the line is ordinary code, prose, an identifier, a hash or a placeholder.",
            "true": "An actual secret value appears: a password, API key, access token, private key or similar credential.",
        },
    }},
    "choice": {"q": {
        "type": "choice",
        "instructions": "Classify this line of text from a developer's screen.",
        "criteria": {
            "secret": "Contains an actual secret credential value: a password, API key, access token or private key.",
            "harmless": "Contains no secret value: ordinary code, prose, identifiers, hashes, file paths or placeholders.",
        },
    }},
}


def secret_probability(answer: dict, kind: str) -> float:
    """Pulls P(secret) out of a Laya answer, whatever exact shape this version returns."""
    if kind == "yes/no":
        for key in ("noul", "probability", "prob", "p_true"):
            if isinstance(answer.get(key), (int, float)):
                return float(answer[key])
        probs = answer.get("probs") or answer.get("probabilities") or {}
        if isinstance(probs, dict):
            for key in ("true", "yes", "A"):
                if key in probs:
                    return float(probs[key])
    else:
        probs = answer.get("probs") or answer.get("probabilities") or {}
        if isinstance(probs, dict) and "secret" in probs:
            return float(probs["secret"])
        if isinstance(probs, (list, tuple)) and len(probs) == 2:
            return float(probs[0])
        if answer.get("choice") in ("secret", "harmless"):
            conf = answer.get("confidence")
            base = 1.0 if answer["choice"] == "secret" else 0.0
            return base if not isinstance(conf, (int, float)) else (conf if base else 1 - conf)
    raise KeyError(f"cannot read probability from {answer!r}")


def auc(scores: list[float], labels: list[bool]) -> float:
    """Chance that a random secret line scores higher than a random harmless line (1.0 = perfect ranking)."""
    pos = [s for s, l in zip(scores, labels) if l]
    neg = [s for s, l in zip(scores, labels) if not l]
    wins = sum((p > n) + 0.5 * (p == n) for p in pos for n in neg)
    return wins / (len(pos) * len(neg))


def report(name: str, flags: list[bool], data, extra: str = "") -> None:
    groups: dict[str, list[bool]] = {}
    for (text, is_secret, group), flag in zip(data, flags):
        groups.setdefault(group, []).append(flag == is_secret)
    tp = sum(f and s for f, (_, s, _) in zip(flags, data))
    fp = sum(f and not s for f, (_, s, _) in zip(flags, data))
    fn = sum((not f) and s for f, (_, s, _) in zip(flags, data))
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    per_group = "  ".join(f"{g}: {sum(v)}/{len(v)}" for g, v in groups.items())
    print(f"{name:<34} recall {recall:5.1%}  precision {precision:5.1%}  missed {fn:2d}  false alarms {fp:2d}  {extra}")
    print(f"{'':<34} {per_group}")
    if VERBOSE:
        for (text, is_secret, _), flag in zip(data, flags):
            if flag != is_secret:
                print(f"      {'MISSED     ' if is_secret else 'FALSE ALARM'} {text[:110]}")


def main() -> None:
    data = cases()
    texts = [t for t, _, _ in data]
    labels = [s for _, s, _ in data]
    print(f"{len(data)} lines: {sum(labels)} with a secret, {len(labels) - sum(labels)} harmless\n")

    report("regex detector (the app)", run_regex(texts), data)
    print()

    import laya
    import torch

    device = "cuda" if torch.cuda.is_available() else "cpu"
    for model_name, kwargs in (("laya (English)", {}), ("laya-multilingual", {"subfolder": "multilingual"})):
        try:
            agent = laya.load("convaiinnovations/laya", device=device, **kwargs)
        except Exception as error:  # noqa: BLE001
            print(f"{model_name}: could not load ({type(error).__name__}: {error})\n")
            continue
        for kind, questions in QUESTIONS.items():
            scores = []
            agent.predict(texts[0], questions)  # warm-up
            start = time.perf_counter()
            for index, text in enumerate(texts):
                result = agent.predict(text, questions)
                if index == 0 and VERBOSE:
                    print(f"      raw answer shape: {result['answers']['q']}")
                scores.append(secret_probability(result["answers"]["q"], kind))
            per_line = (time.perf_counter() - start) * 1000 / len(texts)
            best = max(sorted(set(scores)), key=lambda t: sum((s >= t) == l for s, l in zip(scores, labels)))
            extra = f"{per_line:5.0f} ms/line on {device}, ranking quality {auc(scores, labels):.2f}"
            report(f"{model_name}, {kind}, cut 0.5", [s >= 0.5 for s in scores], data, extra)
            report(f"{model_name}, {kind}, best cut {best:.2f}", [s >= best for s in scores], data)
        print()


if __name__ == "__main__":
    main()
