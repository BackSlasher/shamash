"""Signature verification exactly as docs/home-protocol.md specifies."""
import base64
import hashlib
import time

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

MAX_SKEW_SECONDS = 300


def normalize_public_key(pem: str) -> str:
    """Parse a P-256 public key and return canonical PEM; ValueError if unusable."""
    try:
        key = serialization.load_pem_public_key(pem.encode())
    except Exception as e:
        raise ValueError("bad public key") from e
    if not isinstance(key, ec.EllipticCurvePublicKey) or not isinstance(key.curve, ec.SECP256R1):
        raise ValueError("public key must be ECDSA P-256")
    return key.public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()


def signed_message(timestamp: str, body: bytes) -> bytes:
    return f"{timestamp}\n{hashlib.sha256(body).hexdigest()}".encode()


def check(public_key_pem: str, timestamp: str, body: bytes, signature_b64: str, now: float | None = None) -> str:
    """"ok", "stale" (signature valid but timestamp outside the window) or "bad signature".

    The signature is verified first, so only a holder of the client's key can ever learn "stale"."""
    try:
        ts = int(timestamp)
        key = serialization.load_pem_public_key(public_key_pem.encode())
        key.verify(base64.b64decode(signature_b64, validate=True), signed_message(timestamp, body),
                   ec.ECDSA(hashes.SHA256()))
    except (InvalidSignature, ValueError, TypeError):
        return "bad signature"
    if abs((time.time() if now is None else now) - ts) > MAX_SKEW_SECONDS:
        return "stale"
    return "ok"


def verify(public_key_pem: str, timestamp: str, body: bytes, signature_b64: str, now: float | None = None) -> bool:
    return check(public_key_pem, timestamp, body, signature_b64, now) == "ok"
