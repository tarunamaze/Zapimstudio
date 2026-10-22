"""Integration checks against a temporary database. Run after dotnet build.
Overrides: DOTNET_EXECUTABLE, TEST_PORT (default 5097).
SQL Server instead of SQLite: set TEST_SQLSERVER to a server connection string without a database
(e.g. "Server=localhost;User Id=sa;Password=...;TrustServerCertificate=True") and TEST_SQLCMD to a sqlcmd
command for the same server (e.g. "sqlcmd -S localhost -U sa -P ... -C"). A throwaway database is created and dropped.
No external messages, provider calls or persistent workspace records are created.
"""
import http.cookiejar
import http.server
import json
import os
from pathlib import Path
import shlex
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
DOTNET = os.environ.get("DOTNET_EXECUTABLE", shutil.which("dotnet") or "/workspace/.dotnet/dotnet")
PORT = int(os.environ.get("TEST_PORT", "5097"))
BASE = f"http://127.0.0.1:{PORT}"
PASSWORD = "test-only-password-5289!"
SQLSERVER = os.environ.get("TEST_SQLSERVER", "")
SQLCMD = shlex.split(os.environ.get("TEST_SQLCMD", ""))
DATABASE = f"zapim_smoke_{os.getpid()}"
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


def sql(tmp, query, database=DATABASE):
    """Run a query directly against the app database and return the first value, if any."""
    if SQLSERVER:
        out = subprocess.run(SQLCMD + ["-d", database, "-b", "-y", "0", "-Q", "SET NOCOUNT ON; " + query], capture_output=True, text=True, check=True).stdout.strip()
        return out.splitlines()[0].strip() if out else ""
    with sqlite3.connect(Path(tmp) / "marketing.db") as db:
        rows = db.execute(query).fetchall()
    return rows[0][0] if rows else ""


def minutes_ago(n):
    return f"DATEADD(minute, -{n}, SYSUTCDATETIME())" if SQLSERVER else f"datetime('now', '-{n} minutes')"


def brand(name):
    return {"name": name, "website": "https://example.com", "color": "#087f76", "tone": "Helpful", "audience": "Teams", "description": "A test brand"}


class FakeMetricool(http.server.BaseHTTPRequestHandler):
    """Stands in for app.metricool.com: one brand (4242) with LinkedIn and Facebook connected. Records every request."""
    requests = []

    def handle_one(self):
        if self.headers.get("Transfer-Encoding") == "chunked":
            body = b""
            while size := int(self.rfile.readline().strip(), 16):
                body += self.rfile.read(size)
                self.rfile.readline()
            self.rfile.readline()
        else:
            body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        FakeMetricool.requests.append({"method": self.command, "path": self.path, "auth": self.headers.get("X-Mc-Auth"), "body": json.loads(body) if body else None})
        if self.headers.get("X-Mc-Auth") != "test-metricool-token":
            return self.reply(401, {})
        if self.path.startswith("/admin/simpleProfiles?"):
            return self.reply(200, [{"id": 4242, "label": "Zapim in Metricool", "linkedinCompany": "zapim", "facebookPageId": "123", "instagram": None}])
        if self.path.startswith("/v2/scheduler/posts?") and self.command == "POST":
            return self.reply(200, {"data": {"id": 99}})
        self.reply(404, {})

    do_GET = do_POST = handle_one

    def reply(self, code, data):
        raw = json.dumps(data).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, *args):
        pass


def main():
    global checks
    with tempfile.TemporaryDirectory(prefix="zapim-tests-", ignore_cleanup_errors=True) as tmp:
        metricool = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FakeMetricool)
        threading.Thread(target=metricool.serve_forever, daemon=True).start()
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Development", ASPNETCORE_URLS=BASE, StoragePath=tmp, Publishing__Enabled="false", AI__ApiKey="", Bootstrap__Email="", Bootstrap__Password="", Logging__LogLevel__Default="Warning",
                   Metricool__BaseUrl=f"http://127.0.0.1:{metricool.server_port}", Metricool__UserToken="test-metricool-token", Metricool__UserId="777")
        if SQLSERVER:
            assert SQLCMD, "TEST_SQLCMD is required with TEST_SQLSERVER"
            env["ConnectionStrings__SqlServer"] = f"{SQLSERVER.rstrip(';')};Database={DATABASE}"
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
            protected = sql(tmp, "SELECT ProtectedToken FROM Accounts")
            check("test-token" not in protected and len(protected) > 40, "Social tokens are encrypted at rest")
            check(len(editor.call("/api/audit")) >= 4, "Review history is recorded")
            check(s["metricoolConfigured"], "Session reports Metricool configuration")
            check(admin.call("/api/settings/metricool")["source"] == "server", "Metricool settings fall back to server configuration")
            editor.call("/api/settings/metricool", expected=403)
            editor.call("/api/settings/metricool", "PUT", {"userId": "777", "token": "test-metricool-token"}, expected=403)
            admin.call("/api/settings/metricool", "PUT", {"userId": "abc", "token": "test-metricool-token"}, expected=400)
            check(True, "Only admins can read or change Metricool settings, and the user ID is validated")
            saved = admin.call("/api/settings/metricool", "PUT", {"userId": "777", "token": "test-metricool-token"})
            check(saved["source"] == "app" and "token" not in json.dumps(saved).lower().replace('"token"', ''), "Metricool settings saved from the app never return the token")
            stored = (Path(tmp) / "metricool.json").read_text()
            check("test-metricool-token" not in stored and "777" in stored, "The Metricool token is encrypted at rest")
            check(admin.call("/api/settings/metricool", "PUT", {"userId": "777", "token": ""})["configured"], "A blank token keeps the saved token")
            editor.call(f'/api/brands/{a["id"]}/metricool', "PUT", {"blogId": "4242"}, expected=403)
            admin.call(f'/api/brands/{b["id"]}/metricool', "PUT", {"blogId": "not-a-number"}, expected=400)
            admin.call(f'/api/brands/{b["id"]}/metricool', "PUT", {"blogId": "5555"}, expected=400)
            check(True, "Only admins can map a brand, and only to a Metricool brand that exists")
            mapped = admin.call(f'/api/brands/{b["id"]}/metricool', "PUT", {"blogId": "4242"})
            check(mapped["account"]["name"] == "Zapim in Metricool" and mapped["networks"] == ["LinkedIn", "Facebook"], "Mapping reads the Metricool brand and its connected networks")
            check(admin.call(f'/api/brands/{a["id"]}/metricool')["account"] is None, "Metricool mapping is per brand")
            check(all(r["auth"] == "test-metricool-token" and "blogId=4242" in r["path"] and "userId=777" in r["path"] for r in FakeMetricool.requests if "4242" in r["path"]), "Metricool calls carry the token header, userId and blogId")
            via_metricool = admin.call(f'/api/brands/{b["id"]}/posts', "POST", dict(payload, title="Via Metricool"))
            anon = Client()
            anon.session()
            anon.call("/api/login", "POST", {}, expected=400)
            anon.call("/api/login", "POST", {"email": None, "password": PASSWORD}, expected=400)
            check(True, "Missing or null JSON fields return 400, not 500")
            # Seed scheduled posts directly; neither path below reaches a social API.
            stale = admin.call(f'/api/brands/{a["id"]}/posts', "POST", dict(payload, title="Stale", platform="Facebook"))
            unverified = admin.call(f'/api/brands/{a["id"]}/posts', "POST", dict(payload, title="Unverified", platform="Facebook"))
            sql(tmp, f"UPDATE Posts SET Status='Scheduled', ScheduledAt={minutes_ago(180)} WHERE Id={stale['id']}")
            sql(tmp, f"UPDATE Posts SET Status='Scheduled', ScheduledAt={minutes_ago(1)} WHERE Id={unverified['id']}")
            sql(tmp, f"UPDATE Posts SET Status='Scheduled', ScheduledAt={minutes_ago(1)} WHERE Id={via_metricool['id']}")
            process.terminate()
            process.wait(timeout=10)
            env["Publishing__Enabled"] = "true"
            process = start()
            persisted = Client()
            persisted.login("admin@example.com")
            check(any(p["caption"] == "Revised copy" for p in persisted.call("/api/posts")), "Drafts survive application restart")
            check(not persisted.session()["setupRequired"], "Setup remains closed after restart")
            for _ in range(60):
                posts = {p["id"]: p for p in persisted.call("/api/posts")}
                if all(posts[x["id"]]["status"] not in ("Scheduled", "Publishing") for x in (stale, unverified, via_metricool)):
                    break
                time.sleep(.5)
            check(posts[stale["id"]]["status"] == "NeedsAttention" and "Nothing was sent" in posts[stale["id"]]["error"], "Overdue scheduled posts are not published late")
            check(posts[unverified["id"]]["status"] == "NeedsAttention" and "not been verified" in posts[unverified["id"]]["error"], "Unverified account tokens are never used to publish")
            sent = posts[via_metricool["id"]]
            check(sent["status"] == "Published" and sent["remoteId"] == "metricool:99", "A due post for a mapped brand is handed to Metricool")
            created = [r for r in FakeMetricool.requests if r["method"] == "POST"]
            check(len(created) == 1 and created[0]["body"]["providers"] == [{"network": "linkedin"}] and created[0]["body"]["text"] == "A caption to review" and created[0]["body"]["autoPublish"] is True, "Metricool receives the caption and the post's network")
            instagram = persisted.call(f'/api/brands/{b["id"]}/posts', "POST", dict(payload, title="Instagram via Metricool", platform="Instagram", caption="Caption"))
            instagram = persisted.call(f'/api/posts/{instagram["id"]}/transition', "POST", {"action": "review", "version": instagram["version"]})
            instagram = persisted.call(f'/api/posts/{instagram["id"]}/transition', "POST", {"action": "approve", "version": instagram["version"]})
            persisted.call(f'/api/posts/{instagram["id"]}/transition', "POST", {"action": "publish", "version": instagram["version"]}, expected=400)
            check(True, "Posts for a network not connected in Metricool cannot be scheduled")
            persisted.call("/api/logout", "POST")
            persisted.call("/api/posts", expected=401)
            check(True, "Sign-out removes access")
        finally:
            process.terminate()
            process.wait(timeout=10)
            log.close()
            metricool.shutdown()
            if SQLSERVER:
                try:
                    sql(tmp, f"ALTER DATABASE [{DATABASE}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DATABASE}]", database="master")
                except subprocess.CalledProcessError as e:
                    print(f"WARNING: could not drop test database {DATABASE}: {e.stdout or e.stderr}", flush=True)
    print(f"\n{checks} integration checks passed on {'SQL Server' if SQLSERVER else 'SQLite'}; no external publishing calls made (Metricool is simulated locally).")


if __name__ == "__main__":
    main()
