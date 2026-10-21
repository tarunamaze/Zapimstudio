"""Integration checks against a temporary database. Run after dotnet build.
Overrides: DOTNET_EXECUTABLE, TEST_PORT (default 5097).
No external messages, provider calls or persistent workspace records are created.
"""
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
DOTNET = os.environ.get("DOTNET_EXECUTABLE", shutil.which("dotnet") or "/workspace/.dotnet/dotnet")
PORT = int(os.environ.get("TEST_PORT", "5097"))
BASE = f"http://127.0.0.1:{PORT}"
PASSWORD = "test-only-password-5289!"
checks = 0


def check(value, label):
    global checks
    assert value, label
    checks += 1
    print("PASS", label, flush=True)


class Client:
    def __init__(self):
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.token = ""

    def call(self, path, method="GET", data=None, expected=200, csrf=True):
        headers = {"Content-Type": "application/json"}
        if csrf:
            headers["X-CSRF-TOKEN"] = self.token
        body = json.dumps(data).encode() if data is not None else (b"" if method != "GET" else None)
        req = urllib.request.Request(BASE + path, body, headers, method=method)
        try:
            response = self.opener.open(req, timeout=10)
        except urllib.error.HTTPError as e:
            response = e
        raw = response.read()
        assert response.code == expected, f"{method} {path}: expected {expected}, got {response.code}: {raw[:500]}"
        return json.loads(raw) if raw else {}

    def session(self):
        s = self.call("/api/session")
        self.token = s["csrfToken"]
        return s

    def login(self, email):
        self.session()
        self.call("/api/login", "POST", {"email": email, "password": PASSWORD})
        return self.session()


def brand(name):
    return {"name": name, "website": "https://example.com", "color": "#087f76", "tone": "Helpful", "audience": "Teams", "description": "A test brand"}


def main():
    global checks
    with tempfile.TemporaryDirectory(prefix="zapim-tests-") as tmp:
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Development", ASPNETCORE_URLS=BASE, StoragePath=tmp, Publishing__Enabled="false", AI__ApiKey="", Bootstrap__Email="", Bootstrap__Password="", Logging__LogLevel__Default="Warning")
        log = open(Path(tmp) / "server.log", "w+")
        dll = ROOT / "bin/Debug/net10.0/Zapim.Marketing.dll"
        assert dll.exists(), "Run dotnet build first"

        def start():
            p = subprocess.Popen([DOTNET, str(dll)], cwd=ROOT, env=env, stdout=log, stderr=log)
            for _ in range(80):
                if p.poll() is not None:
                    log.seek(0)
                    raise AssertionError(log.read())
                try:
                    Client().call("/health")
                    return p
                except (urllib.error.URLError, ConnectionError):
                    time.sleep(.15)
            p.terminate()
            raise AssertionError("Server startup timed out")

        process = start()
        try:
            admin = Client()
            s = admin.session()
            check(s["setupRequired"] and s["setupAllowed"], "Fresh development setup is available")
            admin.call("/api/posts", expected=401)
            check(True, "Anonymous API access is denied")
            admin.call("/api/setup", "POST", {"email": "admin@example.com", "password": PASSWORD}, csrf=False, expected=400)
            check(True, "CSRF is required for mutations")
            admin.call("/api/setup", "POST", {"email": "admin@example.com", "password": PASSWORD})
            admin.call("/api/setup", "POST", {"email": "second@example.com", "password": PASSWORD}, expected=409)
            check(True, "First-admin setup cannot be reused")
            s = admin.login("admin@example.com")
            check(s["user"]["role"] == "Admin" and "passwordHash" not in s["user"], "Authenticated session never returns password hash")
            a = admin.call("/api/brands", "POST", brand("Brand A"))
            b = admin.call("/api/brands", "POST", brand("Brand B"))
            for role in ["Editor", "Approver"]:
                admin.call("/api/team", "POST", {"name": role, "email": f"{role.lower()}@example.com", "password": PASSWORD, "role": role, "brandIds": [a["id"]]})
            editor = Client()
            editor.login("editor@example.com")
            check([x["id"] for x in editor.call("/api/brands")] == [a["id"]], "Editor can only list assigned brands")
            editor.call(f'/api/brands/{b["id"]}/assets', expected=404)
            editor.call("/api/brands", "POST", brand("Unauthorized"), expected=403)
            check(True, "Cross-brand assets and admin-only actions are denied")
            payload = {"title": "A real draft", "platform": "LinkedIn", "caption": "A caption to review", "assetId": ""}
            editor.call(f'/api/brands/{b["id"]}/posts', "POST", payload, expected=404)
            post = editor.call(f'/api/brands/{a["id"]}/posts', "POST", payload)
            check(post["status"] == "Draft", "New posts start as drafts")
            pth = f'/api/posts/{post["id"]}'
            editor.call(pth + "/transition", "POST", {"action": "approve", "version": post["version"]}, expected=403)
            check(True, "Editor cannot approve")
            post = editor.call(pth + "/transition", "POST", {"action": "review", "version": post["version"]})
            reviewer = Client()
            reviewer.login("approver@example.com")
            post = reviewer.call(pth + "/transition", "POST", {"action": "approve", "version": post["version"]})
            check(post["status"] == "Approved" and post["approvedBy"], "Assigned approver can approve a reviewed post")
            reviewer.call(pth + "/transition", "POST", {"action": "publish", "version": post["version"]}, expected=400)
            check(True, "Disabled publishing never reports success")
            editor.call(pth, "PUT", dict(payload, version=1), expected=409)
            check(True, "Stale post update is rejected")
            post = editor.call(pth, "PUT", dict(payload, caption="Revised copy", version=post["version"]))
            check(post["status"] == "Draft" and post["approvedBy"] is None, "Editing invalidates approval")
            generated = editor.call(f'/api/brands/{a["id"]}/generate', "POST", {"brief": "Explain helpful customer follow-up", "platform": "Instagram"})
            check(generated["source"] == "Starter template", "Unconfigured AI is clearly labelled as a template")
            private = admin.call(f'/api/brands/{b["id"]}/posts', "POST", dict(payload, title="Private B"))
            editor.call(f'/api/posts/{private["id"]}/transition', "POST", {"action": "review", "version": private["version"]}, expected=404)
            check(all(p["brandId"] == a["id"] for p in editor.call("/api/posts")), "Post listing and direct mutation respect brand isolation")
            account = admin.call(f'/api/brands/{a["id"]}/accounts', "POST", {"platform": "Facebook", "name": "Test only", "externalId": "123456789", "token": "test-token-never-used-externally", "expiresAt": "2035-01-01T00:00:00Z"})
            check("protectedToken" not in account and "token" not in account, "Account responses never expose tokens")
            check(all("protectedToken" not in x for x in editor.call(f'/api/brands/{a["id"]}/accounts')), "Account listing redacts secrets")
            import sqlite3
            with sqlite3.connect(Path(tmp) / "marketing.db") as db:
                protected = db.execute('SELECT ProtectedToken FROM Accounts LIMIT 1').fetchone()[0]
                check("test-token" not in protected and len(protected) > 40, "Social tokens are encrypted at rest")
            check(len(editor.call("/api/audit")) >= 4, "Review history is recorded")
            process.terminate()
            process.wait(timeout=10)
            process = start()
            persisted = Client()
            persisted.login("admin@example.com")
            check(any(p["caption"] == "Revised copy" for p in persisted.call("/api/posts")), "Drafts survive application restart")
            check(not persisted.session()["setupRequired"], "Setup remains closed after restart")
            persisted.call("/api/logout", "POST")
            persisted.call("/api/posts", expected=401)
            check(True, "Sign-out removes access")
        finally:
            process.terminate()
            process.wait(timeout=10)
            log.close()
    print(f"\n{checks} integration checks passed; no external publishing calls made.")


if __name__ == "__main__":
    main()
