#!/usr/bin/env python3
"""Format HTTP request dumps containing JSON bodies and Unicode escapes.

Typical input is an HTTP request captured from the Visual Studio extension:

    POST https://example.test/v1/chat/completions HTTP/1.1
    Content-Type: application/json

    {"model":"...","messages":[{"role":"system","content":"\u4f60\u597d"}]}

The script:
  * decodes JSON Unicode escapes into readable text;
  * pretty-prints messages, tool calls, tool results, and reasoning content;
  * redacts common credential-bearing headers by default;
  * can also handle plain text or raw JSON files.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


SENSITIVE_HEADERS = {
    "authorization",
    "api-key",
    "x-api-key",
    "proxy-authorization",
    "cookie",
    "set-cookie",
}

REQUEST_METHODS = (
    "GET",
    "POST",
    "PUT",
    "PATCH",
    "DELETE",
    "HEAD",
    "OPTIONS",
)


def read_input(path: str) -> str:
    """Read input with a small set of common encodings."""
    if path == "-":
        data = sys.stdin.buffer.read()
    else:
        data = Path(path).read_bytes()

    for encoding in ("utf-8-sig", "utf-8", "utf-16", "gb18030"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue

    return data.decode("utf-8", errors="replace")


def split_http_request(text: str) -> tuple[str, str]:
    """Return (headers, body) when the file looks like an HTTP request dump."""
    first_line = text.splitlines()[0].strip() if text.splitlines() else ""
    if first_line.startswith(("{", "[")):
        return "", text

    looks_like_request = first_line.startswith(
        tuple(f"{method} " for method in REQUEST_METHODS)
    )

    match = re.search(r"\r?\n\r?\n", text)
    if not match:
        return "", text

    headers = text[: match.start()]
    body = text[match.end() :]
    if looks_like_request or ":" in headers:
        return headers, body

    return "", text


def decode_unicode_escapes(text: str) -> str:
    """Decode \\uXXXX and \\UXXXXXXXX escapes outside escaped backslashes.

    JSON parsing already handles normal JSON escapes. This fallback is used for
    malformed dumps or plain-text excerpts where the escapes remain literal.
    """
    if "\\u" not in text and "\\U" not in text:
        return text

    out: list[str] = []
    i = 0
    length = len(text)

    while i < length:
        if text[i] != "\\":
            out.append(text[i])
            i += 1
            continue

        slash_start = i
        while i < length and text[i] == "\\":
            i += 1
        slash_count = i - slash_start

        if i < length and text[i] in ("u", "U") and slash_count % 2 == 1:
            digits_count = 4 if text[i] == "u" else 8
            digits_start = i + 1
            digits_end = digits_start + digits_count
            digits = text[digits_start:digits_end]

            if len(digits) == digits_count and all(
                char in "0123456789abcdefABCDEF" for char in digits
            ):
                out.append("\\" * (slash_count - 1))
                code_point = int(digits, 16)
                i = digits_end

                # Combine a UTF-16 surrogate pair when present.
                if 0xD800 <= code_point <= 0xDBFF and text.startswith("\\u", i):
                    low_digits = text[i + 2 : i + 6]
                    if len(low_digits) == 4 and all(
                        char in "0123456789abcdefABCDEF" for char in low_digits
                    ):
                        low = int(low_digits, 16)
                        if 0xDC00 <= low <= 0xDFFF:
                            code_point = (
                                0x10000
                                + ((code_point - 0xD800) << 10)
                                + (low - 0xDC00)
                            )
                            i += 6

                out.append(chr(code_point))
                continue

        out.append("\\" * slash_count)

    return "".join(out)


def parse_json_document(text: str) -> Any | None:
    """Parse the first JSON value in the body."""
    candidate = text.lstrip("\ufeff \t\r\n")
    if not candidate:
        return None

    try:
        value, _ = json.JSONDecoder().raw_decode(candidate)
        return value
    except json.JSONDecodeError:
        return None


def pretty_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2)


def max_backtick_run(text: str) -> int:
    return max((len(match.group(0)) for match in re.finditer(r"`+", text)), default=0)


def code_block(text: str, language: str = "") -> str:
    fence = "`" * max(3, max_backtick_run(text) + 1)
    return f"{fence}{language}\n{text.rstrip()}\n{fence}"


def truncate(text: str, limit: int) -> str:
    if limit <= 0 or len(text) <= limit:
        return text

    omitted = len(text) - limit
    return f"{text[:limit]}\n\n... ({omitted} characters omitted)"


def format_content(content: Any) -> str:
    if content is None:
        return ""

    if isinstance(content, str):
        return decode_unicode_escapes(content)

    if isinstance(content, list):
        parts: list[str] = []
        for index, part in enumerate(content, start=1):
            if not isinstance(part, dict):
                parts.append(f"[Part {index}]\n{decode_unicode_escapes(str(part))}")
                continue

            part_type = str(part.get("type", "unknown"))
            if part_type == "text":
                text = str(part.get("text", ""))
                parts.append(decode_unicode_escapes(text))
                continue

            if part_type in {"image_url", "input_image"}:
                image = part.get("image_url") or part.get("image") or part
                if isinstance(image, dict):
                    image = image.get("url") or image.get("data") or pretty_json(image)
                image_text = str(image)
                if image_text.startswith("data:") and len(image_text) > 160:
                    image_text = image_text[:157] + "..."
                parts.append(f"[Image {index}]\n{image_text}")
                continue

            parts.append(f"[Part {index}: {part_type}]\n{pretty_json(part)}")

        return "\n\n".join(parts)

    return pretty_json(content)


def format_tool_arguments(arguments: Any) -> str:
    if isinstance(arguments, str):
        decoded = decode_unicode_escapes(arguments)
        try:
            parsed = json.loads(decoded)
        except json.JSONDecodeError:
            return decoded
        return pretty_json(parsed)

    return pretty_json(arguments)


def redact_header(name: str, value: str, enabled: bool) -> str:
    if not enabled or name.lower() not in SENSITIVE_HEADERS:
        return value

    stripped = value.strip()
    if stripped.lower().startswith("bearer "):
        token = stripped[7:]
        return f"Bearer {token[:8]}..." if token else "Bearer <redacted>"

    return "<redacted>"


def render_headers(headers: str, redact: bool) -> list[str]:
    lines = headers.splitlines()
    if not lines:
        return []

    output = ["## HTTP Request", ""]
    request_line = lines[0].strip()
    if request_line:
        output.extend([f"**Request:** `{request_line}`", ""])

    rendered_headers: list[tuple[str, str]] = []
    for line in lines[1:]:
        if ":" not in line:
            continue
        name, value = line.split(":", 1)
        rendered_headers.append(
            (name.strip(), redact_header(name.strip(), value.strip(), redact))
        )

    if rendered_headers:
        output.extend(["**Headers:**", ""])
        for name, value in rendered_headers:
            output.append(f"- `{name}`: {value}")
        output.append("")

    return output


def render_message(message: dict[str, Any], index: int, max_chars: int) -> list[str]:
    role = str(message.get("role", "unknown"))
    name = message.get("name")
    title = f"### Message {index}: `{role}`"
    if name:
        title += f" (`{name}`)"

    lines = [title, ""]

    tool_call_id = message.get("tool_call_id")
    if tool_call_id:
        lines.extend([f"- Tool call ID: `{tool_call_id}`", ""])

    content = format_content(message.get("content"))
    if content:
        lines.extend(
            [
                "#### Content",
                "",
                truncate(content, max_chars),
                "",
            ]
        )

    reasoning = format_content(message.get("reasoning_content"))
    if reasoning:
        lines.extend(
            [
                "#### Reasoning",
                "",
                truncate(reasoning, max_chars),
                "",
            ]
        )

    tool_calls = message.get("tool_calls")
    if isinstance(tool_calls, list):
        for tool_index, tool_call in enumerate(tool_calls, start=1):
            if not isinstance(tool_call, dict):
                lines.extend(
                    [
                        f"#### Tool Call {tool_index}",
                        "",
                        pretty_json(tool_call),
                        "",
                    ]
                )
                continue

            function = tool_call.get("function")
            if isinstance(function, dict):
                function_name = function.get("name", "unknown")
                arguments = function.get("arguments", "")
            else:
                function_name = tool_call.get("name", "unknown")
                arguments = tool_call.get("arguments", "")

            call_id = tool_call.get("id")
            lines.append(f"#### Tool Call {tool_index}: `{function_name}`")
            lines.append("")
            if call_id:
                lines.extend([f"- ID: `{call_id}`", ""])
            lines.append("**Arguments:**")
            lines.append("")
            lines.append(code_block(format_tool_arguments(arguments), "json"))
            lines.append("")

    if not content and not reasoning and not tool_calls:
        lines.extend(["_(empty message)_", ""])

    lines.extend(["---", ""])
    return lines


def render_payload(payload: Any, source: str, max_chars: int) -> list[str]:
    lines = ["## Request Body", ""]

    if not isinstance(payload, dict):
        lines.extend(["### JSON Payload", "", code_block(pretty_json(payload), "json"), ""])
        return lines

    model = payload.get("model")
    if model:
        lines.extend([f"**Model:** `{model}`", ""])

    metadata_keys = (
        "stream",
        "temperature",
        "top_p",
        "max_tokens",
        "response_format",
        "tool_choice",
    )
    metadata = {key: payload[key] for key in metadata_keys if key in payload}
    if metadata:
        lines.extend(["## Request Options", "", code_block(pretty_json(metadata), "json"), ""])

    messages = payload.get("messages")
    if isinstance(messages, list):
        lines.extend([f"## Messages ({len(messages)})", ""])
        for index, message in enumerate(messages, start=1):
            if isinstance(message, dict):
                lines.extend(render_message(message, index, max_chars))
            else:
                lines.extend(
                    [
                        f"### Message {index}",
                        "",
                        code_block(pretty_json(message), "json"),
                        "",
                        "---",
                        "",
                    ]
                )

    other_keys = [
        key
        for key in payload
        if key not in {"model", "messages", *metadata_keys}
    ]
    if other_keys:
        remaining = {key: payload[key] for key in other_keys}
        lines.extend(["## Other Fields", "", code_block(pretty_json(remaining), "json"), ""])

    return lines


def build_output(
    raw_text: str,
    source: str,
    output_json: bool,
    redact: bool,
    max_chars: int,
) -> str:
    headers, body = split_http_request(raw_text)
    payload = parse_json_document(body)

    if output_json:
        if payload is not None:
            return pretty_json(payload) + "\n"
        return decode_unicode_escapes(body).rstrip() + "\n"

    lines = [
        "# Formatted Request Dump",
        "",
        f"**Source:** `{source}`",
        "",
    ]
    lines.extend(render_headers(headers, redact))
    if payload is None:
        lines.extend(
            [
                "## Text Body",
                "",
                "> JSON parsing failed; displaying decoded text.",
                "",
                truncate(decode_unicode_escapes(body), max_chars),
                "",
            ]
        )
    else:
        lines.extend(render_payload(payload, source, max_chars))

    return "\n".join(lines).rstrip() + "\n"


def write_stdout(text: str) -> None:
    """Write UTF-8 directly so Windows console code pages do not corrupt output."""
    data = text.encode("utf-8")
    buffer = getattr(sys.stdout, "buffer", None)
    if buffer is not None:
        buffer.write(data)
        buffer.flush()
    else:
        sys.stdout.write(text)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Decode Unicode escapes in HTTP/JSON dumps and format messages, "
            "tool calls, and tool results as readable Markdown."
        )
    )
    parser.add_argument(
        "input",
        nargs="?",
        default="-",
        help="Input file. Use '-' or omit to read from stdin.",
    )
    parser.add_argument(
        "-o",
        "--output",
        help="Output file. Use '-' for stdout. Defaults to '<input>.formatted.md'.",
    )
    parser.add_argument(
        "--stdout",
        action="store_true",
        help="Write to stdout instead of a file.",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        dest="output_json",
        help="Output decoded/pretty-printed JSON instead of Markdown.",
    )
    parser.add_argument(
        "--no-redact",
        action="store_true",
        help="Do not redact Authorization, API key, and cookie headers.",
    )
    parser.add_argument(
        "--max-chars",
        type=int,
        default=0,
        help="Truncate each message/reasoning block to this many characters. 0 = unlimited.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    raw_text = read_input(args.input)
    output = build_output(
        raw_text=raw_text,
        source="<stdin>" if args.input == "-" else args.input,
        output_json=args.output_json,
        redact=not args.no_redact,
        max_chars=max(0, args.max_chars),
    )

    output_path = args.output
    if args.stdout or output_path == "-":
        write_stdout(output)
        return 0

    if output_path is None:
        if args.input == "-":
            write_stdout(output)
            return 0
        output_path = f"{args.input}.formatted.md"

    path = Path(output_path)
    path.write_text(output, encoding="utf-8")
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
