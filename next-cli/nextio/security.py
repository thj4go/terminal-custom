import os
import re


WRITE_TOOLS = {
    "create_folder",
    "create_file",
    "append_text",
    "replace_text",
    "apply_patch",
    "write_binary",
    "base64_decode_file",
    "copy_path",
    "move_path",
    "delete_path",
}

DESTRUCTIVE_TOOLS = {
    "delete_path",
    "move_path",
    "write_binary",
    "base64_decode_file",
}

INSTALL_PATTERNS = [
    r"\bpip\s+install\b",
    r"\bnpm\s+install\b",
    r"\byarn\s+add\b",
    r"\bpnpm\s+add\b",
    r"\bwinget\s+install\b",
    r"\bchoco\s+install\b",
    r"\bscoop\s+install\b",
]

DANGEROUS_TERMINAL_PATTERNS = [
    r"\bRemove-Item\b.*\s-(Recurse|r)\b",
    r"\brm\s+(-rf|-fr)\b",
    r"\brmdir\b.*\s/(s|q)\b",
    r"\bdel\b.*\s/(s|q)\b",
    r"\bFormat-Volume\b",
    r"\bformat\s+[a-z]:",
    r"\bdiskpart\b",
    r"\bSet-ExecutionPolicy\b",
    r"\breg\s+(delete|add)\b",
    r"\bshutdown\b",
    r"\brestart-computer\b",
    r"\bstop-computer\b",
]


def permission_mode():
    return os.getenv("NEXT_IO_PERMISSION_MODE", "guarded").strip().lower()


def installs_allowed():
    return os.getenv("NEXT_IO_ALLOW_INSTALLS", "false").strip().lower() in {"1", "true", "yes", "sim", "on"}


def absolute_paths_allowed():
    return os.getenv("NEXT_IO_ALLOW_ABSOLUTE_PATHS", "false").strip().lower() in {"1", "true", "yes", "sim", "on"}


def is_install_command(command):
    text = str(command or "")
    return any(re.search(pattern, text, re.IGNORECASE) for pattern in INSTALL_PATTERNS)


def is_dangerous_terminal_command(command):
    text = str(command or "")
    return any(re.search(pattern, text, re.IGNORECASE) for pattern in DANGEROUS_TERMINAL_PATTERNS)


def check_terminal_command(command):
    mode = permission_mode()
    if mode == "free":
        return True, ""
    if is_install_command(command) and not installs_allowed():
        return False, "instaladores bloqueados; defina NEXT_IO_ALLOW_INSTALLS=true para liberar"
    if is_dangerous_terminal_command(command):
        return False, "comando destrutivo bloqueado pelo modo guarded"
    return True, ""


def check_path_scope(path, workspace_root):
    if permission_mode() == "free" or absolute_paths_allowed():
        return True, ""
    try:
        resolved = os.path.abspath(path)
        root = os.path.abspath(workspace_root)
        common = os.path.commonpath([resolved, root])
    except Exception:
        return False, "nao foi possivel validar o escopo do caminho"
    if common != root:
        return False, "caminho absoluto fora do workspace bloqueado"
    return True, ""


def check_tool_action(name, arguments=None):
    mode = permission_mode()
    args = arguments if isinstance(arguments, dict) else {}
    if mode == "free":
        return True, ""
    if name in DESTRUCTIVE_TOOLS and not bool(args.get("confirm")):
        return False, f"{name} exige confirm=true no modo guarded"
    return True, ""

