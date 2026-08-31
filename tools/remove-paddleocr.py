"""Programmatically strip PaddleOCR / OpenCvSharp from the project.

Used by the "Sync no-local-paddleocr" workflow to derive the No-Local-OCR
variant from master. It removes:
  * the PaddleOCR / OpenCvSharp package references and native-DLL copy items
    from the .csproj,
  * the PaddleOCR engine wrapper + its call sites in Services/OcrService.cs,
  * the PaddleOCR switch arm in Services/FileParserService.cs,
  * and injects an arm64 InstallationTarget into source.extension.vsixmanifest
    so the No-Local-OCR VSIX installs on both x64 and ARM64.
"""

import re
import sys
from pathlib import Path

UTF8 = {"encoding": "utf-8"}


def read(path):
    return Path(path).read_text(**UTF8).splitlines()


def write(path, lines):
    Path(path).write_text("\n".join(lines) + "\n", **UTF8)


def scrub_csproj():
    pat = re.compile(
        r"Sdcb\.PaddleOCR|Sdcb\.PaddleInference|OpenCvSharp4\.runtime\.win"
        r"|paddle_inference_c\.dll|paddle2onnx\.dll|libiomp5md\.dll|mkldnn\.dll"
        r"|mklml\.dll|onnxruntime\.dll|onnxruntime_providers_shared\.dll"
        r"|common\.dll|OpenCvSharpExtern\.dll"
    )
    lines = read("DeepSeek_v4_for_VisualStudio.csproj")
    kept = [ln for ln in lines if not pat.search(ln)]
    write("DeepSeek_v4_for_VisualStudio.csproj", kept)


def scrub_ocr_service():
    lines = read("Services/OcrService.cs")
    out = []
    in_region = False
    for ln in lines:
        if "#region" in ln and "PaddleOCR Engine Wrapper" in ln:
            in_region = True
            continue
        if in_region:
            if "#endregion" in ln:
                in_region = False
            continue
        if re.search(r"^\s*PaddleOCR,\s*$", ln):            # enum member
            continue
        if ln.lstrip().startswith("///") and "PaddleOCR" in ln:  # stale doc comments
            continue
        if re.search(r"OcrEngineType\.PaddleOCR\s*=>", ln):  # switch arms
            continue
        if re.search(r"PaddleEngineWrapper\.", ln):          # method calls
            continue
        out.append(ln)
    write("Services/OcrService.cs", out)


def scrub_file_parser():
    lines = read("Services/FileParserService.cs")
    kept = [ln for ln in lines if not re.search(r"OcrEngineType\.PaddleOCR\s*=>", ln)]
    write("Services/FileParserService.cs", kept)


def inject_arm64():
    lines = read("source.extension.vsixmanifest")
    text = Path("source.extension.vsixmanifest").read_text(**UTF8)
    if "arm64" in text:
        return  # already injected
    out = []
    injected = False
    for ln in lines:
        out.append(ln)
        if not injected and "</InstallationTarget>" in ln:
            out.append('    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[17.14, )">')
            out.append("      <ProductArchitecture>arm64</ProductArchitecture>")
            out.append("    </InstallationTarget>")
            injected = True
    write("source.extension.vsixmanifest", out)


def verify():
    critical = re.compile(
        r"Sdcb\.PaddleOCR|Sdcb\.PaddleInference|OpenCvSharp4\.runtime\.win"
        r"|OpenCvSharp\.Cv2|PaddleEngineWrapper|OcrEngineType\.PaddleOCR"
    )
    problems = []
    for path in (
        "DeepSeek_v4_for_VisualStudio.csproj",
        "Services/OcrService.cs",
        "Services/FileParserService.cs",
    ):
        for i, ln in enumerate(read(path), 1):
            if ln.lstrip().startswith("//"):
                continue  # skip comments
            if critical.search(ln):
                problems.append(f"{path}:{i}: {ln.strip()}")
    if problems:
        print("::error::PaddleOCR/OpenCvSharp still referenced after removal")
        for p in problems:
            print(p)
        sys.exit(1)


def main():
    scrub_csproj()
    scrub_ocr_service()
    scrub_file_parser()
    inject_arm64()
    verify()
    print("PaddleOCR removed; ARM64 manifest target injected")


if __name__ == "__main__":
    main()