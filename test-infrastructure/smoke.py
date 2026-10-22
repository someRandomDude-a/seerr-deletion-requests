"""Isolated Jellyfin 12.2 integration checks. Never point this at an existing server."""
import json
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:8096"
AUTH = 'MediaBrowser Client="DeletionQueueSmoke", Device="IsolatedFixture", DeviceId="dq-smoke", Version="0.1"'


def value(obj, key):
    return next((v for k, v in obj.items() if k.lower() == key.lower()), None)


def call(method, path, data=None, token=None, expected=200, origin=None):
    headers = {"Authorization": AUTH + (', Token="' + token + '"' if token else '')}
    if origin:
        headers["Origin"] = origin
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(BASE + path, data=json.dumps(data).encode() if data is not None else None,
                                     headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    body = response.read()
    allowed = (expected,) if isinstance(expected, int) else expected
    assert response.code in allowed, (method, path, response.code, body[:300])
    try:
        return json.loads(body)
    except (ValueError, UnicodeDecodeError):
        return body.decode(errors="replace")


for attempt in range(90):
    try:
        info = call("GET", "/System/Info/Public")
        break
    except (OSError, AssertionError):
        time.sleep(1)
else:
    raise RuntimeError("Isolated Jellyfin did not start")

assert not value(info, "StartupWizardCompleted"), "Refusing to modify an already-configured Jellyfin instance"
call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, expected=204)
call("GET", "/Startup/User")
call("POST", "/Startup/User", {"Name": "dq-admin", "Password": "SyntheticFixture123!"}, expected=204)
call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False}, expected=204)
call("POST", "/Startup/Complete", expected=204)
admin_login = call("POST", "/Users/AuthenticateByName", {"Username": "dq-admin", "Pw": "SyntheticFixture123!"})
admin = value(admin_login, "AccessToken")
admin_id = value(value(admin_login, "User"), "Id")
assert admin, "Missing administrator user session"

call("GET", "/DeletionQueue/proposals", expected=(401, 403))
status = call("GET", "/DeletionQueue/admin/status", token=admin)
assert value(status, "StorageAvailable") is True, status
assert value(status, "ExecutionAvailable") is True
assert value(status, "IndependentRollbackProtection") is False
assert value(value(status, "Policy"), "AutomaticDeletion") is False
print("PASS: native plugin loads, authenticated store opens, destructive execution defaults off")

pages = call("GET", "/web/ConfigurationPages?enableInMainMenu=true", token=admin)
assert any(value(page, "Name") == "DeletionQueueAdmin" for page in pages)
html = call("GET", "/web/ConfigurationPage?name=DeletionQueueAdmin", token=admin)
assert "Authenticated transition journal" in html
assert "Save policy" in html
assert "dq-admin" not in html
assert "getUrl('DeletionQueue/" in call("GET", "/web/ConfigurationPage?name=DeletionQueueAdmin.js", token=admin)
assert "dq-web-client" in call("GET", "/web/index.html")
for removed in ("/DeletionQueue", "/DeletionQueue/ui", "/DeletionQueue/assets/user.js", "/DeletionQueue/assets/theme.js",
                "/DeletionQueue/assets/native-styles", "/DeletionQueue/assets/queue.css"):
    call("GET", removed, token=admin, expected=404)
print("PASS: native page registration and embedded resources; ordinary shell contains no private state")

viewer = call("POST", "/Users/New", {"Name": "dq-viewer"}, token=admin)
viewer_id = value(viewer, "Id")
viewer_login = call("POST", "/Users/AuthenticateByName", {"Username": "dq-viewer", "Pw": ""})
viewer_token = value(viewer_login, "AccessToken")
call("GET", "/DeletionQueue/proposals", token=viewer_token)
call("GET", "/DeletionQueue/admin/status", token=viewer_token, expected=(401, 403))
call("GET", "/web/ConfigurationPages", token=viewer_token, expected=(401, 403))
print("PASS: ordinary user queue access; native admin page discovery and private evidence are elevated")

import uuid
command = str(uuid.uuid4())
pref = call("GET", "/DeletionQueue/me/preferences", token=viewer_token)
body = {"commandId": command, "revision": value(pref, "Revision"), "delayHours": 96, "notifications": False}
saved = call("PATCH", "/DeletionQueue/me/preferences", body, viewer_token)
assert value(saved, "DelayHours") == 96
call("PATCH", "/DeletionQueue/me/preferences", body, viewer_token, expected=409)
call("PATCH", "/DeletionQueue/me/preferences", {**body, "commandId": str(uuid.uuid4()), "revision": value(saved, "Revision")}, viewer_token,
     expected=403, origin="https://attacker.invalid")
print("PASS: explicit preferences, command replay refusal and cross-origin mutation rejection")

secret = "SYNTHETIC-SEERR-KEY-DO-NOT-LOG"
connection = value(status, "Seerr")
call("PATCH", "/DeletionQueue/admin/seerr", {"commandId": str(uuid.uuid4()), "revision": value(connection, "Revision"),
     "baseUrl": "http://127.0.0.1:59999", "apiKey": secret}, admin, expected=204)
policy = value(status, "Policy")
policy["AutomaticDeletion"] = True
call("PATCH", "/DeletionQueue/admin/policy", {"commandId": str(uuid.uuid4()), "revision": value(policy, "Revision"), "policy": policy}, admin, expected=204)
new_status = call("GET", "/DeletionQueue/admin/status", token=admin)
assert value(value(new_status, "Policy"), "AutomaticDeletion") is True
assert value(new_status, "ExecutionAvailable") is True
assert value(value(new_status, "Policy"), "SeerrManagedDeletionAcknowledged") is False
assert secret not in json.dumps(new_status)
call("POST", "/DeletionQueue/admin/execute", {}, admin, expected=404)
print("PASS: explicit opt-in does not bypass missing safety contracts; credentials are not returned")

before = value(new_status, "Journal")
for path in ("/DeletionQueue/proposals", "/DeletionQueue/me/preferences", "/DeletionQueue/me/notices"):
    call("GET", path, token=viewer_token)
after = value(call("GET", "/DeletionQueue/admin/status", token=admin), "Journal")
assert [e for e in before if value(e, "Actor") not in ("collector", "jellyfin-playback")] == [
    e for e in after if value(e, "Actor") not in ("collector", "jellyfin-playback")]
print("PASS: user reads, notice opening and admin monitoring produce no authorization changes")

call("POST", "/Auth/Keys?app=DeletionQueueSmoke", token=admin, expected=204)
keys = call("GET", "/Auth/Keys", token=admin)
items = value(keys, "Items") if isinstance(keys, dict) else keys
api_key = next(value(k, "AccessToken") for k in items if value(k, "AppName") == "DeletionQueueSmoke")
call("GET", "/DeletionQueue/admin/status", token=api_key, expected=(401, 403))
call("GET", "/DeletionQueue/proposals", token=api_key, expected=(401, 403))
print("PASS: Jellyfin service API keys cannot impersonate user or administrator sessions")

viewer_record = call("GET", "/Users/" + viewer_id, token=admin)
viewer_policy = value(viewer_record, "Policy")
viewer_policy["IsAdministrator"] = True
call("POST", "/Users/" + viewer_id + "/Policy", viewer_policy, admin, expected=204)
admin_record = call("GET", "/Users/" + admin_id, token=viewer_token)
admin_policy = value(admin_record, "Policy")
admin_policy["IsAdministrator"] = False
call("POST", "/Users/" + admin_id + "/Policy", admin_policy, viewer_token, expected=204)
call("GET", "/DeletionQueue/admin/status", token=admin, expected=(401, 403))
print("PASS: live demotion is checked even with an existing authenticated session")
print("All isolated Jellyfin smoke checks passed. No media was mounted and no deletion was attempted.")
