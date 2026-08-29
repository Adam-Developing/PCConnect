from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / "AndroidV2" / "app" / "src" / "main" / "res" / "xml"
ANDROID = "{http://schemas.android.com/apk/res/android}"

manifest = ElementTree.parse(ROOT / "AndroidV2" / "app" / "src" / "main" / "AndroidManifest.xml").getroot()
application = manifest.find("application")
assert application is not None
assert application.attrib[f"{ANDROID}allowBackup"] == "false"
assert application.attrib[f"{ANDROID}networkSecurityConfig"] == "@xml/network_security_config"

required_domains = {"root", "file", "database", "sharedpref"}
legacy = ElementTree.parse(RES / "backup_rules.xml").getroot()
assert {node.attrib["domain"] for node in legacy.findall("exclude")} == required_domains

modern = ElementTree.parse(RES / "data_extraction_rules.xml").getroot()
for destination in ("cloud-backup", "device-transfer"):
    node = modern.find(destination)
    assert node is not None
    assert {exclude.attrib["domain"] for exclude in node.findall("exclude")} == required_domains

network = ElementTree.parse(RES / "network_security_config.xml").getroot()
base = network.find("base-config")
assert base is not None and base.attrib.get("cleartextTrafficPermitted") == "false"

debug_network = ElementTree.parse(ROOT / "AndroidV2" / "app" / "src" / "debug" / "res" / "xml" / "network_security_config.xml").getroot()
debug_base = debug_network.find("base-config")
assert debug_base is not None and debug_base.attrib.get("cleartextTrafficPermitted") == "false"
debug_domains = debug_network.findall("domain-config")
assert len(debug_domains) == 1 and debug_domains[0].attrib.get("cleartextTrafficPermitted") == "true"
assert {domain.text for domain in debug_domains[0].findall("domain")} == {"localhost", "127.0.0.1"}

gradle = (ROOT / "AndroidV2" / "app" / "build.gradle.kts").read_text(encoding="utf-8")
assert 'PCCONNECT_ANDROID_DEBUG_API_BASE_URL' in gradle
assert 'debugUsesLoopbackHttp' in gradle

repository = (ROOT / "AndroidV2" / "app" / "src" / "main" / "java" / "com" / "adamkhattab" / "pcconnect" / "v2" / "data" / "ControllerRepository.kt").read_text(encoding="utf-8")
assert "Commands are deliberately never queued locally" in repository
assert "PendingCommand" not in repository
assert "toEntity(localPiiCipher)" in repository
assert "decryptReminder(row.id, row.text)" in repository

cipher = (ROOT / "AndroidV2" / "app" / "src" / "main" / "java" / "com" / "adamkhattab" / "pcconnect" / "v2" / "data" / "LocalPiiCipher.kt").read_text(encoding="utf-8")
assert 'KeyStore.getInstance("AndroidKeyStore")' in cipher
assert 'Cipher.getInstance("AES/GCM/NoPadding")' in cipher

database = (ROOT / "AndroidV2" / "app" / "src" / "main" / "java" / "com" / "adamkhattab" / "pcconnect" / "v2" / "data" / "AppDatabase.kt").read_text(encoding="utf-8")
assert 'db.execSQL("DELETE FROM reminders")' in database

manifest_text = (ROOT / "AndroidV2" / "app" / "src" / "main" / "AndroidManifest.xml").read_text(encoding="utf-8")
assert 'android:autoVerify="true"' in manifest_text
assert 'android:pathPrefix="/verify-email"' in manifest_text
assert 'android:pathPrefix="/reset-password"' in manifest_text

print("Android backup, transport, encrypted-cache, verified-link, and no-offline-command-queue security invariants passed.")
