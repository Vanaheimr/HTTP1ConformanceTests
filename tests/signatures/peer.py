"""
A foreign RFC 9421 peer for the demo host: pyauth's http-message-signatures,
pinned by tests/signatures.sh, signing requests the demo verifies on
/signatures/verify and verifying responses the demo signs on
/signatures/signed/{keyid}.

Usage:  python peer.py <base>      e.g. http://127.0.0.1:8080

Prints one line per check, tab separated: PASS|FAIL|SKIP, name, detail. Exits
1 on any FAIL. The keys are RFC 9421's own examples (Appendix B.1), the same
ones Demo/DemoSignatureKeys.cs holds, so neither side hands the other a key.

The library is not this repository's code and does not share its reading of
the RFC, which is the reason to use it. It is first checked against the RFC's
own examples: a peer that cannot reproduce them proves nothing about the demo,
and one that cannot fail is no witness either, so a tampered example has to be
refused before anything is sent.
"""

import base64
import datetime
import hashlib
import http.client
import ssl
import sys
import types
import typing
import urllib.parse

# http-message-signatures 2.0.1 vendors http_sfv, whose item.py and list.py
# import SupportsIndex from typing_extensions, a package it does not declare.
# typing has had SupportsIndex since Python 3.8, so the one name is lent from
# there rather than a second package fetched for it.
try:
    import typing_extensions  # noqa: F401
except ModuleNotFoundError:
    sys.modules["typing_extensions"] = types.ModuleType("typing_extensions")
    sys.modules["typing_extensions"].SupportsIndex = typing.SupportsIndex

from http_message_signatures import (HTTPMessageSigner, HTTPMessageVerifier, HTTPSignatureKeyResolver,
                                     InvalidSignature, algorithms)
from http_message_signatures.structures import CaseInsensitiveDict

# --------------------------------------------------------------------------
# RFC 9421, Appendix B.1
# --------------------------------------------------------------------------

RSA_PUBLIC = b"""-----BEGIN RSA PUBLIC KEY-----
MIIBCgKCAQEAhAKYdtoeoy8zcAcR874L8cnZxKzAGwd7v36APp7Pv6Q2jdsPBRrw
WEBnez6d0UDKDwGbc6nxfEXAy5mbhgajzrw3MOEt8uA5txSKobBpKDeBLOsdJKFq
MGmXCQvEG7YemcxDTRPxAleIAgYYRjTSd/QBwVW9OwNFhekro3RtlinV0a75jfZg
kne/YiktSvLG34lw2zqXBDTC5NHROUqGTlML4PlNZS5Ri2U4aCNx2rUPRcKIlE0P
uKxI4T+HIaFpv8+rdV6eUgOrB2xeI1dSFFn/nnv5OoZJEIB+VmuKn3DCUcCZSFlQ
PSXSfBDiUGhwOw76WuSSsf1D4b/vLoJ10wIDAQAB
-----END RSA PUBLIC KEY-----
"""

RSA_PRIVATE = b"""-----BEGIN RSA PRIVATE KEY-----
MIIEqAIBAAKCAQEAhAKYdtoeoy8zcAcR874L8cnZxKzAGwd7v36APp7Pv6Q2jdsP
BRrwWEBnez6d0UDKDwGbc6nxfEXAy5mbhgajzrw3MOEt8uA5txSKobBpKDeBLOsd
JKFqMGmXCQvEG7YemcxDTRPxAleIAgYYRjTSd/QBwVW9OwNFhekro3RtlinV0a75
jfZgkne/YiktSvLG34lw2zqXBDTC5NHROUqGTlML4PlNZS5Ri2U4aCNx2rUPRcKI
lE0PuKxI4T+HIaFpv8+rdV6eUgOrB2xeI1dSFFn/nnv5OoZJEIB+VmuKn3DCUcCZ
SFlQPSXSfBDiUGhwOw76WuSSsf1D4b/vLoJ10wIDAQABAoIBAG/JZuSWdoVHbi56
vjgCgkjg3lkO1KrO3nrdm6nrgA9P9qaPjxuKoWaKO1cBQlE1pSWp/cKncYgD5WxE
CpAnRUXG2pG4zdkzCYzAh1i+c34L6oZoHsirK6oNcEnHveydfzJL5934egm6p8DW
+m1RQ70yUt4uRc0YSor+q1LGJvGQHReF0WmJBZHrhz5e63Pq7lE0gIwuBqL8SMaA
yRXtK+JGxZpImTq+NHvEWWCu09SCq0r838ceQI55SvzmTkwqtC+8AT2zFviMZkKR
Qo6SPsrqItxZWRty2izawTF0Bf5S2VAx7O+6t3wBsQ1sLptoSgX3QblELY5asI0J
YFz7LJECgYkAsqeUJmqXE3LP8tYoIjMIAKiTm9o6psPlc8CrLI9CH0UbuaA2JCOM
cCNq8SyYbTqgnWlB9ZfcAm/cFpA8tYci9m5vYK8HNxQr+8FS3Qo8N9RJ8d0U5Csw
DzMYfRghAfUGwmlWj5hp1pQzAuhwbOXFtxKHVsMPhz1IBtF9Y8jvgqgYHLbmyiu1
mwJ5AL0pYF0G7x81prlARURwHo0Yf52kEw1dxpx+JXER7hQRWQki5/NsUEtv+8RT
qn2m6qte5DXLyn83b1qRscSdnCCwKtKWUug5q2ZbwVOCJCtmRwmnP131lWRYfj67
B/xJ1ZA6X3GEf4sNReNAtaucPEelgR2nsN0gKQKBiGoqHWbK1qYvBxX2X3kbPDkv
9C+celgZd2PW7aGYLCHq7nPbmfDV0yHcWjOhXZ8jRMjmANVR/eLQ2EfsRLdW69bn
f3ZD7JS1fwGnO3exGmHO3HZG+6AvberKYVYNHahNFEw5TsAcQWDLRpkGybBcxqZo
81YCqlqidwfeO5YtlO7etx1xLyqa2NsCeG9A86UjG+aeNnXEIDk1PDK+EuiThIUa
/2IxKzJKWl1BKr2d4xAfR0ZnEYuRrbeDQYgTImOlfW6/GuYIxKYgEKCFHFqJATAG
IxHrq1PDOiSwXd2GmVVYyEmhZnbcp8CxaEMQoevxAta0ssMK3w6UsDtvUvYvF22m
qQKBiD5GwESzsFPy3Ga0MvZpn3D6EJQLgsnrtUPZx+z2Ep2x0xc5orneB5fGyF1P
WtP+fG5Q6Dpdz3LRfm+KwBCWFKQjg7uTxcjerhBWEYPmEMKYwTJF5PBG9/ddvHLQ
EQeNC8fHGg4UXU8mhHnSBt3EA10qQJfRDs15M38eG2cYwB1PZpDHScDnDA0=
-----END RSA PRIVATE KEY-----
"""

RSA_PSS_PUBLIC = b"""-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAr4tmm3r20Wd/PbqvP1s2
+QEtvpuRaV8Yq40gjUR8y2Rjxa6dpG2GXHbPfvMs8ct+Lh1GH45x28Rw3Ry53mm+
oAXjyQ86OnDkZ5N8lYbggD4O3w6M6pAvLkhk95AndTrifbIFPNU8PPMO7OyrFAHq
gDsznjPFmTOtCEcN2Z1FpWgchwuYLPL+Wokqltd11nqqzi+bJ9cvSKADYdUAAN5W
Utzdpiy6LbTgSxP7ociU4Tn0g5I6aDZJ7A8Lzo0KSyZYoA485mqcO0GVAdVw9lq4
aOT9v6d+nb4bnNkQVklLQ3fVAvJm+xdDOp9LCNCN48V2pnDOkFV6+U9nV5oyc6XI
2wIDAQAB
-----END PUBLIC KEY-----
"""

RSA_PSS_PRIVATE = b"""-----BEGIN PRIVATE KEY-----
MIIEvgIBADALBgkqhkiG9w0BAQoEggSqMIIEpgIBAAKCAQEAr4tmm3r20Wd/Pbqv
P1s2+QEtvpuRaV8Yq40gjUR8y2Rjxa6dpG2GXHbPfvMs8ct+Lh1GH45x28Rw3Ry5
3mm+oAXjyQ86OnDkZ5N8lYbggD4O3w6M6pAvLkhk95AndTrifbIFPNU8PPMO7Oyr
FAHqgDsznjPFmTOtCEcN2Z1FpWgchwuYLPL+Wokqltd11nqqzi+bJ9cvSKADYdUA
AN5WUtzdpiy6LbTgSxP7ociU4Tn0g5I6aDZJ7A8Lzo0KSyZYoA485mqcO0GVAdVw
9lq4aOT9v6d+nb4bnNkQVklLQ3fVAvJm+xdDOp9LCNCN48V2pnDOkFV6+U9nV5oy
c6XI2wIDAQABAoIBAQCUB8ip+kJiiZVKF8AqfB/aUP0jTAqOQewK1kKJ/iQCXBCq
pbo360gvdt05H5VZ/RDVkEgO2k73VSsbulqezKs8RFs2tEmU+JgTI9MeQJPWcP6X
aKy6LIYs0E2cWgp8GADgoBs8llBq0UhX0KffglIeek3n7Z6Gt4YFge2TAcW2WbN4
XfK7lupFyo6HHyWRiYHMMARQXLJeOSdTn5aMBP0PO4bQyk5ORxTUSeOciPJUFktQ
HkvGbym7KryEfwH8Tks0L7WhzyP60PL3xS9FNOJi9m+zztwYIXGDQuKM2GDsITeD
2mI2oHoPMyAD0wdI7BwSVW18p1h+jgfc4dlexKYRAoGBAOVfuiEiOchGghV5vn5N
RDNscAFnpHj1QgMr6/UG05RTgmcLfVsI1I4bSkbrIuVKviGGf7atlkROALOG/xRx
DLadgBEeNyHL5lz6ihQaFJLVQ0u3U4SB67J0YtVO3R6lXcIjBDHuY8SjYJ7Ci6Z6
vuDcoaEujnlrtUhaMxvSfcUJAoGBAMPsCHXte1uWNAqYad2WdLjPDlKtQJK1diCm
rqmB2g8QE99hDOHItjDBEdpyFBKOIP+NpVtM2KLhRajjcL9Ph8jrID6XUqikQuVi
4J9FV2m42jXMuioTT13idAILanYg8D3idvy/3isDVkON0X3UAVKrgMEne0hJpkPL
FYqgetvDAoGBAKLQ6JZMbSe0pPIJkSamQhsehgL5Rs51iX4m1z7+sYFAJfhvN3Q/
OGIHDRp6HjMUcxHpHw7U+S1TETxePwKLnLKj6hw8jnX2/nZRgWHzgVcY+sPsReRx
NJVf+Cfh6yOtznfX00p+JWOXdSY8glSSHJwRAMog+hFGW1AYdt7w80XBAoGBAImR
NUugqapgaEA8TrFxkJmngXYaAqpA0iYRA7kv3S4QavPBUGtFJHBNULzitydkNtVZ
3w6hgce0h9YThTo/nKc+OZDZbgfN9s7cQ75x0PQCAO4fx2P91Q+mDzDUVTeG30mE
t2m3S0dGe47JiJxifV9P3wNBNrZGSIF3mrORBVNDAoGBAI0QKn2Iv7Sgo4T/XjND
dl2kZTXqGAk8dOhpUiw/HdM3OGWbhHj2NdCzBliOmPyQtAr770GITWvbAI+IRYyF
S7Fnk6ZVVVHsxjtaHy1uJGFlaZzKR4AGNaUTOJMs6NadzCmGPAxNQQOCqoUjn4XR
rOjr9w349JooGXhOxbu8nOxX
-----END PRIVATE KEY-----
"""

ECC_P256_PUBLIC = b"""-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEqIVYZVLCrPZHGHjP17CTW0/+D9Lf
w0EkjqF7xB4FivAxzic30tMM4GF+hR6Dxh71Z50VGGdldkkDXZCnTNnoXQ==
-----END PUBLIC KEY-----
"""

ECC_P256_PRIVATE = b"""-----BEGIN EC PRIVATE KEY-----
MHcCAQEEIFKbhfNZfpDsW43+0+JjUr9K+bTeuxopu653+hBaXGA7oAoGCCqGSM49
AwEHoUQDQgAEqIVYZVLCrPZHGHjP17CTW0/+D9Lfw0EkjqF7xB4FivAxzic30tMM
4GF+hR6Dxh71Z50VGGdldkkDXZCnTNnoXQ==
-----END EC PRIVATE KEY-----
"""

ED25519_PUBLIC = b"""-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEAJrQLj5P/89iXES9+vFgrIy29clF9CC/oPPsw3c5D0bs=
-----END PUBLIC KEY-----
"""

ED25519_PRIVATE = b"""-----BEGIN PRIVATE KEY-----
MC4CAQAwBQYDK2VwBCIEIJ+DYvh6SEqVTm50DFtMDoQikTmiCqirVv9mWG9qfSnF
-----END PRIVATE KEY-----
"""

SHARED_SECRET_B64 = "uzvJfB4u3N0Jy4T7NZ75MDVcr8zSTInedJtkgcu46YW4XByzNJjxBdtjUkdJPBtbmHhIDi6pcl8jsasjlTMtDQ=="

PRIVATE = {
    "test-key-rsa":       RSA_PRIVATE,
    "test-key-rsa-pss":   RSA_PSS_PRIVATE,
    "test-key-ecc-p256":  ECC_P256_PRIVATE,
    "test-key-ed25519":   ED25519_PRIVATE,
    "test-shared-secret": base64.b64decode(SHARED_SECRET_B64),
}

PUBLIC = {
    "test-key-rsa":       RSA_PUBLIC,
    "test-key-rsa-pss":   RSA_PSS_PUBLIC,
    "test-key-ecc-p256":  ECC_P256_PUBLIC,
    "test-key-ed25519":   ED25519_PUBLIC,
    "test-shared-secret": base64.b64decode(SHARED_SECRET_B64),
}

ALGORITHM = {
    "test-key-rsa":       algorithms.RSA_V1_5_SHA256,
    "test-key-rsa-pss":   algorithms.RSA_PSS_SHA512,
    "test-key-ecc-p256":  algorithms.ECDSA_P256_SHA256,
    "test-key-ed25519":   algorithms.ED25519,
    "test-shared-secret": algorithms.HMAC_SHA256,
}


class Keys(HTTPSignatureKeyResolver):

    def __init__(self, alias=None):
        self.alias = alias or {}

    def resolve_private_key(self, key_id):
        return PRIVATE[self.alias.get(key_id, key_id)]

    def resolve_public_key(self, key_id):
        return PUBLIC[self.alias.get(key_id, key_id)]


class Request:

    def __init__(self, method, url, headers, body=b""):
        self.method  = method
        self.url     = url
        self.headers = CaseInsensitiveDict(headers)
        self.body    = body


class Response:

    def __init__(self, status_code, headers, request, body):
        self.status_code = status_code
        self.headers     = CaseInsensitiveDict(headers)
        self.request     = request
        self.url         = request.url
        self.body        = body


failed = False


def report(verdict, name, detail=""):
    global failed
    if verdict == "FAIL":
        failed = True
    print(f"{verdict}\t{name}\t{detail}".replace("\n", " "), flush=True)


def check(name, condition, detail=""):
    report("PASS" if condition else "FAIL", name, "" if condition else detail)


def signer(key_id, alias=None):
    return HTTPMessageSigner(signature_algorithm=ALGORITHM[key_id], key_resolver=Keys(alias))


def verifier(key_id):
    return HTTPMessageVerifier(signature_algorithm=ALGORITHM[key_id], key_resolver=Keys())


def verifies(message, key_id, **kwargs):
    try:
        verifier(key_id).verify(message, **kwargs)
        return True, ""
    except InvalidSignature as e:
        return False, str(e)


def digest(body):
    return "sha-256=:" + base64.b64encode(hashlib.sha256(body).digest()).decode() + ":"


# --------------------------------------------------------------------------
# The library against the RFC (Appendix B.2), before anything is sent
# --------------------------------------------------------------------------

RFC_URL      = "https://example.com/foo?param=Value&Pet=dog"
RFC_REQUEST  = {
    "Host":           "example.com",
    "Date":           "Tue, 20 Apr 2021 02:07:55 GMT",
    "Content-Type":   "application/json",
    "Content-Digest": "sha-512=:WZDPaVn/7XgHaAy8pmojAkGWoRx2UFChF41A2svX+TaPm+AbwAgBWnrIiYllu7BNNyealdVLvRwEmTHWXvJwew==:",
    "Content-Length": "18",
}
RFC_RESPONSE = {
    "Date":           "Tue, 20 Apr 2021 02:07:56 GMT",
    "Content-Type":   "application/json",
    "Content-Digest": "sha-512=:mEWXIS7MaLRuGgxOBdODa3xqM1XdEvxoYhvlCFJ41QJgJc4GTsPp29l5oGX69wWdXymyU0rjJuahq4l5aGgfLQ==:",
    "Content-Length": "23",
}
RFC_AGE      = datetime.timedelta(days=365 * 100)    # created=1618884473, in 2021

RFC_EXAMPLES = [
    # name, key, request (or None for the response), Signature-Input, Signature
    ("b23-rsa-pss", "test-key-rsa-pss", True,
     'sig-b23=("date" "@method" "@path" "@query" "@authority" "content-type" "content-digest" "content-length");created=1618884473;keyid="test-key-rsa-pss"',
     None),
    ("b24-ecdsa-p256", "test-key-ecc-p256", False,
     'sig-b24=("@status" "content-type" "content-digest" "content-length");created=1618884473;keyid="test-key-ecc-p256"',
     "sig-b24=:wNmSUAhwb5LxtOtOpNa6W5xj067m5hFrj0XQ4fvpaCLx0NKocgPquLgyahnzDnDAUy5eCdlYUEkLIj+32oiasw==:"),
    ("b25-hmac", "test-shared-secret", True,
     'sig-b25=("date" "@authority" "content-type");created=1618884473;keyid="test-shared-secret"',
     "sig-b25=:pxcQw6G3AjtMBQjwo8XzkZf/bws5LelbaMk5rGIGtE8=:"),
    ("b26-ed25519", "test-key-ed25519", True,
     'sig-b26=("date" "@method" "@path" "@authority" "content-type" "content-length");created=1618884473;keyid="test-key-ed25519"',
     "sig-b26=:wqcAqbmYJ2ji2glfAMaRy4gruYYnx2nEFN2HN6jrnDnQCK1u02Gb04v9EDgwUPiu4A0w6vuQv5lIp5WPpBKRCw==:"),
]

B23_SIGNATURE = "sig-b23=:bbN8oArOxYoyylQQUU6QYwrTuaxLwjAC9fbY2F6SVWvh0yBiMIRGOnMYwZ/5MR6fb0Kh1rIRASVxFkeGt683+qRpRRU5p2voTp768ZrCUb38K0fUxN0O0iC59DzYx8DFll5GmydPxSmme9v6ULbMFkl+V5B1TP/yPViV7KsLNmvKiLJH1pFkh/aYA2HXXZzNBXmIkoQoLd7YfW91kE9o/CCoC1xMy7JA1ipwvKvfrs65ldmlu9bpG6A9BmzhuzF8Eim5f8ui9eH8LZH896+QIF61ka39VBrohr9iyMUJpvRX2Zbhl5ZJzSRxpJyoEZAFL2FUo5fTIztsDZKEgM4cUA==:"


def rfc_message(is_request, extra):
    request = Request("POST", RFC_URL, {**RFC_REQUEST})
    if is_request:
        request.headers.update(extra)
        return request
    return Response(200, {**RFC_RESPONSE, **extra}, request, b"")


def calibrate():

    for name, key_id, is_request, signature_input, signature in RFC_EXAMPLES:

        if signature is None:
            signature = B23_SIGNATURE

        message = rfc_message(is_request, {"Signature-Input": signature_input, "Signature": signature})
        ok, why = verifies(message, key_id, max_age=RFC_AGE)
        check(f"rfc/{name}", ok, f"the library does not verify the RFC's own example: {why}")

        # The same example, one covered field changed: a verifier that passes
        # this cannot tell a signature from a string.
        tampered = rfc_message(is_request, {"Signature-Input": signature_input, "Signature": signature})
        tampered.headers["Content-Type"] = "application/json; charset=utf-8"
        ok, _ = verifies(tampered, key_id, max_age=RFC_AGE)
        check(f"rfc/{name}-tampered", not ok, "a changed Content-Type still verified")

    # Ed25519 and HMAC are deterministic, so the library's signature must be
    # the RFC's, octet for octet.
    for name, key_id, covered, expected in [
        ("b25-hmac-sign",    "test-shared-secret", ("date", "@authority", "content-type"),
         "pxcQw6G3AjtMBQjwo8XzkZf/bws5LelbaMk5rGIGtE8="),
        ("b26-ed25519-sign", "test-key-ed25519",   ("date", "@method", "@path", "@authority", "content-type", "content-length"),
         "wqcAqbmYJ2ji2glfAMaRy4gruYYnx2nEFN2HN6jrnDnQCK1u02Gb04v9EDgwUPiu4A0w6vuQv5lIp5WPpBKRCw=="),
    ]:
        message = rfc_message(True, {})
        signer(key_id).sign(message, key_id=key_id, label="sig", include_alg=False,
                            created=datetime.datetime.fromtimestamp(1618884473), covered_component_ids=covered)
        got = message.headers["Signature"]
        check(f"rfc/{name}", got == f"sig=:{expected}:", f"got {got}")

    # A known deviation of the library, pinned rather than passed: it names
    # the component of RFC 9421, Section 2.2.8, "@query-params", and refuses
    # "@query-param", so a signature over a query parameter is one it can
    # neither make nor verify. The check turns red when that changes, and is
    # then to be replaced by a client check signing "@query-param".
    message = rfc_message(True, {})
    try:
        signer("test-key-ed25519").sign(message, key_id="test-key-ed25519", label="sig",
                                        covered_component_ids=('"@query-param";name="Pet"',))
        check("known/library-refuses-query-param", False,
              "the library now signs \"@query-param\": unpin this, and sign with it against the demo")
    except Exception as e:
        check("known/library-refuses-query-param", "@query-param" in str(e), f"refused, but for: {e}")


# --------------------------------------------------------------------------
# On the wire
# --------------------------------------------------------------------------

BASE     = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://127.0.0.1:8080"
SPLIT    = urllib.parse.urlsplit(BASE)


def connection():
    if SPLIT.scheme == "https":
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode    = ssl.CERT_NONE       # the demo's certificate is self-signed
        return http.client.HTTPSConnection(SPLIT.hostname, SPLIT.port, timeout=20, context=context)
    return http.client.HTTPConnection(SPLIT.hostname, SPLIT.port, timeout=20)


def send(request, target=None):
    """Send the request as it is, every field once and in order, and nothing
    http.client would add on its own: no Accept-Encoding, a Host only if the
    request has one."""
    target = target or urllib.parse.urlsplit(request.url)._replace(scheme="", netloc="").geturl()
    conn   = connection()
    conn.putrequest(request.method, target, skip_host=True, skip_accept_encoding=True)
    for name, value in request.headers.items():
        conn.putheader(name, value)
    conn.endheaders(request.body or None)
    raw     = conn.getresponse()
    body    = raw.read()
    headers = {}
    for name, value in raw.getheaders():
        headers[name] = headers[name] + ", " + value if name in headers else value
    conn.close()
    return Response(raw.status, headers, request, body)


def new_request(method="GET", path="/signatures/verify?peer=python", body=b"", content_type=None):
    headers = {"Host": SPLIT.netloc}
    if body:
        headers["Content-Type"]   = content_type or "application/json"
        headers["Content-Digest"] = digest(body)
        headers["Content-Length"] = str(len(body))
    return Request(method, BASE + path, headers, body)


def expect(name, response, status, starts):
    text = response.body.decode("utf-8", "replace").strip()
    check(name, response.status_code == status and text.startswith(starts),
          f"{response.status_code} {text[:200]}")


def clients():

    # Every key, the request covering every derived component the library
    # has that a request carries, @scheme and @target-uri included: those two
    # are the demo's to derive from the connection, the request line does not
    # name them.
    for key_id in ALGORITHM:
        request = new_request()
        signer(key_id).sign(request, key_id=key_id, label="py",
                            covered_component_ids=("@method", "@authority", "@path", "@query", "@scheme",
                                                   "@target-uri", "@request-target", "host"))
        expect(f"client/{key_id}", send(request), 200, f"Valid (py) {key_id} {ALGORITHM[key_id].algorithm_id}")

    # A body, under a digest under the signature.
    body    = b'{"hello": "world"}'
    request = new_request("POST", "/signatures/verify", body)
    signer("test-key-ed25519").sign(request, key_id="test-key-ed25519", label="py",
                                    covered_component_ids=("@method", "@authority", "@path", "content-digest",
                                                           "content-type", "content-length"))
    expect("client/post-content-digest", send(request), 200, "Valid (py)")

    # The same signature over a digest of other content: the signature is
    # valid, and the demo must still refuse the body.
    request = new_request("POST", "/signatures/verify", body)
    request.headers["Content-Digest"] = digest(b'{"hello": "earth"}')
    signer("test-key-ed25519").sign(request, key_id="test-key-ed25519", label="py",
                                    covered_component_ids=("@method", "@authority", "@path", "content-digest",
                                                           "content-type", "content-length"))
    expect("client/post-content-digest-mismatch", send(request), 403, "Content-Digest Mismatch")

    # Signed for one query, sent with another.
    request = new_request()
    signer("test-key-ed25519").sign(request, key_id="test-key-ed25519", label="py",
                                    covered_component_ids=("@method", "@authority", "@path", "@query"))
    expect("client/other-query", send(request, "/signatures/verify?peer=someone-else"), 403, "Invalid (py)")

    # What the demo asks of a signature, each refused for its own reason.
    expect("client/unsigned", send(new_request()), 403, "NotPresent")

    request = new_request()
    signer("test-key-ed25519").sign(request, key_id="test-key-ed25519", label="py",
                                    covered_component_ids=("@method", "@authority"))
    expect("client/insufficient-coverage", send(request), 403, "InsufficientCoverage (py)")

    request = new_request()
    signer("test-key-ed25519").sign(request, key_id="test-key-ed25519", label="py",
                                    created=datetime.datetime.now() - datetime.timedelta(hours=1),
                                    covered_component_ids=("@method", "@authority", "@path"))
    expect("client/too-old", send(request), 403, "TooOld (py)")

    request = new_request()
    signer("test-key-ed25519", alias={"test-key-unknown": "test-key-ed25519"}).sign(
        request, key_id="test-key-unknown", label="py", covered_component_ids=("@method", "@authority", "@path"))
    expect("client/unknown-key", send(request), 403, "UnknownKey (py)")

    # The library's name for @query-param, which RFC 9421 does not have: a
    # derived component the demo does not know fails the signature base
    # (Section 2.5) rather than being read as a field.
    request = new_request()
    signer("test-key-ed25519").sign(request, key_id="test-key-ed25519", label="py",
                                    covered_component_ids=("@method", "@authority", "@path",
                                                           '"@query-params";name="peer"'))
    expect("client/query-params-is-not-rfc-9421", send(request), 403, "SignatureBaseError (py)")


def servers():

    for key_id in ALGORITHM:

        request  = new_request(path=f"/signatures/signed/{key_id}")
        request.headers["Accept-Encoding"] = "gzip"
        response = send(request)

        if key_id == "test-key-rsa":
            # Its primes have 1088 and 960 bits, which the BCL cannot import:
            # the demo only verifies with it, and says so.
            check(f"server/{key_id}", response.status_code == 404, f"{response.status_code}")
            continue

        if response.status_code != 200:
            check(f"server/{key_id}", False, f"{response.status_code} {response.body[:200]!r}")
            continue

        ok, why = verifies(response, key_id)
        check(f"server/{key_id}", ok, f"{why}; Signature-Input: {response.headers.get('Signature-Input')}")

        # The signature covers the digest; the digest has to cover the content.
        check(f"server/{key_id}-content-digest", response.headers.get("Content-Digest") == digest(response.body),
              f"Content-Digest {response.headers.get('Content-Digest')} for {len(response.body)} octets")

        # Asked for gzip, and signed: compressing would have changed the
        # Content-Length and Content-Digest the signature covers.
        check(f"server/{key_id}-not-compressed", "Content-Encoding" not in response.headers,
              f"Content-Encoding: {response.headers.get('Content-Encoding')}")

        response.headers["Content-Type"] = "text/html"
        ok, _ = verifies(response, key_id)
        check(f"server/{key_id}-tampered", not ok, "the response verified with its Content-Type changed")


if __name__ == "__main__":
    calibrate()
    clients()
    servers()
    # The driver reads a peer without this line as one that died halfway.
    print("END\t\t", flush=True)
    sys.exit(1 if failed else 0)
