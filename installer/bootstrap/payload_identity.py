"""Offline bootstrap build checks; these do not authenticate a publisher."""
import hashlib
import re
from pathlib import Path


def validate_payload_pin(pin, version, source_commit, payload=None):
    if not isinstance(pin, dict) or pin.get('version') != version:
        raise ValueError('Bootstrap payload version differs from source; build a fresh full installer')
    if (not re.fullmatch('[a-f0-9]{40}', source_commit) or pin.get('sourceCommit') != source_commit or
            pin.get('payloadSourceCommit') != source_commit):
        raise ValueError('Bootstrap source and payload identities differ')
    if (type(pin.get('bytes')) is not int or not 0 < pin['bytes'] <= 2147483648 or
            not isinstance(pin.get('sha256'), str) or not re.fullmatch('[a-f0-9]{64}', pin['sha256'])):
        raise ValueError('Invalid bootstrap payload pin')
    if payload is not None:
        path = Path(payload)
        if path.is_symlink() or not path.is_file() or path.stat().st_nlink != 1 or path.stat().st_size != pin['bytes']:
            raise ValueError('Bootstrap full installer bytes differ')
        with path.open('rb') as source:
            if hashlib.file_digest(source, 'sha256').hexdigest() != pin['sha256']:
                raise ValueError('Bootstrap full installer digest differs')
    return pin


def require_installer_description(description, version, source_commit):
    if (not isinstance(description, dict) or description.get('artifact') != 'Yahad.Setup' or
            description.get('version') != version or description.get('sourceCommit') != source_commit or
            description.get('setupSourceCommit') != source_commit or description.get('coreSourceCommit') != source_commit or
            description.get('systemOperationsInvoked') is not False or description.get('machineAcceptance') is not False):
        raise ValueError('Retained installer embedded release/source identity differs')
    return description
