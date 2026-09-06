#!/usr/bin/env python3
"""Offline structural validation for the Statesman repository.

This does not replace compilation. It catches broken project references, malformed
configuration, missing central package versions, invalid relative documentation
links, obvious placeholder code, and unbalanced C# delimiters before `dotnet` is
available.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable
from urllib.parse import unquote

try:
    import yaml  # type: ignore
except ImportError:  # pragma: no cover - optional on minimal runners
    yaml = None


@dataclass(frozen=True)
class Finding:
    category: str
    path: Path
    message: str


class Validator:
    def __init__(self, root: Path) -> None:
        self.root = root.resolve()
        self.findings: list[Finding] = []
        self.metrics: dict[str, int] = {}

    def error(self, category: str, path: Path, message: str) -> None:
        self.findings.append(Finding(category, path, message))

    def run(self) -> None:
        self.validate_required_files()
        self.validate_json()
        self.validate_xml_and_projects()
        self.validate_directory_build_props_chaining()
        self.validate_solution()
        self.validate_yaml()
        self.validate_workflow_expectations()
        self.validate_markdown_links()
        self.validate_text_hygiene()
        self.validate_csharp_delimiters()
        self.collect_metrics()

    def validate_required_files(self) -> None:
        required = [
            "Statesman.slnx",
            "README.md",
            "LICENSE",
            "CHANGELOG.md",
            "ROADMAP.md",
            "CONTRIBUTING.md",
            "SECURITY.md",
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json",
            "version.json",
            ".github/workflows/ci.yml",
            ".github/workflows/docs.yml",
            ".github/workflows/dependabot-auto-merge.yml",
            ".github/dependabot.yml",
            "docs/docfx.json",
            "docs/toc.yml",
        ]
        for relative in required:
            path = self.root / relative
            if not path.is_file():
                self.error("required", path, "required repository file is missing")

    def validate_json(self) -> None:
        paths = sorted(self.root.rglob("*.json"))
        self.metrics["json_files"] = len(paths)
        for path in paths:
            if self._ignored(path):
                continue
            try:
                json.loads(path.read_text(encoding="utf-8"))
            except Exception as exc:
                self.error("json", path, str(exc))

    def validate_xml_and_projects(self) -> None:
        xml_paths = sorted({*self.root.rglob("*.csproj"), *self.root.rglob("*.props"), *self.root.rglob("*.targets")})
        package_versions_path = self.root / "Directory.Packages.props"
        central_packages: set[str] = set()
        try:
            central_tree = ET.parse(package_versions_path)
            central_packages = {
                node.attrib["Include"]
                for node in central_tree.findall(".//PackageVersion")
                if "Include" in node.attrib
            }
        except Exception as exc:
            self.error("xml", package_versions_path, str(exc))

        projects = sorted(self.root.rglob("*.csproj"))
        self.metrics["projects"] = len(projects)
        for project in projects:
            if self._ignored(project):
                continue
            if not self._has_effective_target_framework(project):
                self.error(
                    "target-framework",
                    project,
                    "project has no TargetFramework/TargetFrameworks and does not inherit one from Directory.Build.props",
                )

        for path in xml_paths:
            if self._ignored(path):
                continue
            try:
                tree = ET.parse(path)
            except Exception as exc:
                self.error("xml", path, str(exc))
                continue

            for reference in tree.findall(".//ProjectReference"):
                include = reference.attrib.get("Include")
                if not include:
                    self.error("project-reference", path, "ProjectReference is missing Include")
                    continue
                target = (path.parent / include.replace("\\", "/")).resolve()
                if not target.is_file():
                    self.error("project-reference", path, f"missing project target: {include}")

            for reference in tree.findall(".//PackageReference"):
                package = reference.attrib.get("Include") or reference.attrib.get("Update")
                if not package:
                    self.error("package", path, "PackageReference is missing Include or Update")
                    continue
                if "Version" in reference.attrib or "VersionOverride" in reference.attrib:
                    self.error("package", path, f"{package} bypasses central package management")
                if package not in central_packages:
                    self.error("package", path, f"{package} has no PackageVersion in Directory.Packages.props")

    def validate_directory_build_props_chaining(self) -> None:
        """Ensure nested Directory.Build.props files do not silently shadow repository policy."""
        root_props = (self.root / "Directory.Build.props").resolve()
        for path in sorted(self.root.rglob("Directory.Build.props")):
            if path.resolve() == root_props or self._ignored(path):
                continue
            try:
                tree = ET.parse(path)
            except Exception:
                continue

            imports = []
            for node in tree.findall(".//Import"):
                project = node.attrib.get("Project")
                if not project or "$(" in project:
                    continue
                imports.append((path.parent / project.replace("\\", "/")).resolve())

            if root_props not in imports:
                self.error(
                    "directory-build-props",
                    path,
                    "nested Directory.Build.props shadows repository build policy without importing the root props",
                )

    def validate_solution(self) -> None:
        solution = self.root / "Statesman.slnx"
        if not solution.is_file():
            return
        try:
            tree = ET.parse(solution)
        except Exception as exc:
            self.error("xml", solution, str(exc))
            return

        listed = {
            path.replace("\\", "/")
            for node in tree.findall(".//Project")
            if (path := node.attrib.get("Path"))
        }
        actual = {
            path.relative_to(self.root).as_posix()
            for path in self.root.rglob("*.csproj")
            if not self._ignored(path)
        }
        for missing in sorted(actual - listed):
            self.error("solution", solution, f"project is not included: {missing}")
        for stale in sorted(listed - actual):
            self.error("solution", solution, f"solution references a missing project: {stale}")

    def validate_yaml(self) -> None:
        paths = sorted({*self.root.rglob("*.yml"), *self.root.rglob("*.yaml")})
        self.metrics["yaml_files"] = len(paths)
        for path in paths:
            if self._ignored(path):
                continue
            text = path.read_text(encoding="utf-8")
            if "\t" in text:
                self.error("yaml", path, "YAML contains a tab character")
            if yaml is not None:
                try:
                    yaml.safe_load(text)
                except Exception as exc:
                    self.error("yaml", path, str(exc))

    def validate_workflow_expectations(self) -> None:
        ci_path = self.root / ".github/workflows/ci.yml"
        self._require_text(
            ci_path,
            "workflow",
            [
                ("--coverage", "CI workflow must collect test coverage"),
                ("--coverage-output-format cobertura", "CI workflow must emit Cobertura coverage"),
                ("codecov/codecov-action", "CI workflow must upload coverage to Codecov"),
                ("reportgenerator", "CI workflow must generate a coverage summary report"),
            ],
        )

        docs_path = self.root / ".github/workflows/docs.yml"
        self._require_text(
            docs_path,
            "workflow",
            [
                ("docfx", "Docs workflow must build the DocFX site"),
                ("actions/deploy-pages", "Docs workflow must deploy to GitHub Pages"),
            ],
        )

        release_path = self.root / ".github/workflows/release.yml"
        self._require_text(
            release_path,
            "workflow",
            [
                ("dotnet nuget push", "Release workflow must support NuGet publishing"),
                ("SHA256SUMS.txt", "Release workflow must publish package checksums"),
            ],
        )

        dependabot_path = self.root / ".github/dependabot.yml"
        self._require_text(
            dependabot_path,
            "workflow",
            [
                ("groups:", "Dependabot configuration must group updates"),
                ("update-types:", "Dependabot groups must distinguish non-breaking and major updates"),
            ],
        )

        dependabot_merge_path = self.root / ".github/workflows/dependabot-auto-merge.yml"
        self._require_text(
            dependabot_merge_path,
            "workflow",
            [
                ("pull_request_target", "Dependabot auto-merge workflow must react to pull requests"),
                ("gh pr merge --auto", "Dependabot auto-merge workflow must enable GitHub auto-merge"),
            ],
        )

    def validate_markdown_links(self) -> None:
        paths = sorted(self.root.rglob("*.md"))
        self.metrics["markdown_files"] = len(paths)
        link_pattern = re.compile(r"(?<!!)\[[^\]]*\]\(([^)]+)\)")
        for path in paths:
            if self._ignored(path):
                continue
            text = self._without_fenced_code(path.read_text(encoding="utf-8"))
            for raw_target in link_pattern.findall(text):
                target = raw_target.strip()
                if target.startswith("<") and target.endswith(">"):
                    target = target[1:-1]
                target = target.split(maxsplit=1)[0]
                if not target or target.startswith(("#", "http://", "https://", "mailto:", "sandbox:")):
                    continue
                target = unquote(target.split("#", 1)[0])
                candidate = (path.parent / target).resolve()
                try:
                    candidate.relative_to(self.root)
                except ValueError:
                    self.error("markdown-link", path, f"relative link escapes repository: {raw_target}")
                    continue
                if not candidate.exists():
                    self.error("markdown-link", path, f"relative link target does not exist: {raw_target}")

    def validate_text_hygiene(self) -> None:
        text_suffixes = {".cs", ".csproj", ".props", ".targets", ".json", ".yml", ".yaml", ".md", ".ps1", ".sh", ".py", ".mmd", ".slnx"}
        placeholder = re.compile(r"\b(?:TODO|FIXME|NotImplementedException)\b")
        for path in sorted(self.root.rglob("*")):
            if not path.is_file() or self._ignored(path) or path.suffix.lower() not in text_suffixes:
                continue
            text = path.read_text(encoding="utf-8")
            if text and not text.endswith("\n"):
                self.error("hygiene", path, "file does not end with a newline")
            if path.suffix.lower() not in {".md", ".sln"}:
                for number, line in enumerate(text.splitlines(), start=1):
                    if line.rstrip(" \t") != line:
                        self.error("hygiene", path, f"trailing whitespace on line {number}")
                        break
            if path.suffix == ".cs" and placeholder.search(text):
                self.error("placeholder", path, "source contains TODO, FIXME, or NotImplementedException")

    def validate_csharp_delimiters(self) -> None:
        paths = sorted(self.root.rglob("*.cs"))
        self.metrics["csharp_files"] = len(paths)
        for path in paths:
            if self._ignored(path):
                continue
            text = path.read_text(encoding="utf-8")
            error = self._balanced_csharp(text)
            if error:
                self.error("csharp-balance", path, error)

            # Relational patterns require compile-time constants. BCL static properties
            # such as TimeSpan.Zero look constant-like but are not legal pattern operands.
            invalid_relational = re.search(
                r"\bis\s*(?:<=|>=|<|>)\s*(?:TimeSpan|DateTime|DateTimeOffset)\.",
                text,
            )
            if invalid_relational:
                line = text.count("\n", 0, invalid_relational.start()) + 1
                self.error(
                    "csharp-pattern",
                    path,
                    f"relational pattern uses a non-constant BCL member near line {line}",
                )

    def collect_metrics(self) -> None:
        self.metrics["test_cases"] = 0
        self.metrics["source_lines"] = 0
        for path in self.root.rglob("*.cs"):
            if self._ignored(path):
                continue
            text = path.read_text(encoding="utf-8")
            self.metrics["source_lines"] += len(text.splitlines())
            if "tests" in path.parts:
                self.metrics["test_cases"] += len(re.findall(r"\[(?:Fact|Theory)\b", text))
        self.metrics["all_files"] = sum(1 for path in self.root.rglob("*") if path.is_file() and not self._ignored(path))

    def report(self) -> str:
        status = "PASS" if not self.findings else "FAIL"
        lines = [f"Statesman static validation: {status}", ""]
        for key in sorted(self.metrics):
            lines.append(f"{key}: {self.metrics[key]}")
        lines.append("")
        if self.findings:
            lines.append("Findings:")
            for finding in sorted(self.findings, key=lambda value: (value.category, str(value.path), value.message)):
                try:
                    display = finding.path.relative_to(self.root)
                except ValueError:
                    display = finding.path
                lines.append(f"- [{finding.category}] {display}: {finding.message}")
        else:
            lines.append("No structural findings.")
        lines.append("")
        lines.append("Note: this validator is intentionally offline and does not replace dotnet restore, build, test, or pack.")
        return "\n".join(lines) + "\n"

    def _require_text(self, path: Path, category: str, expectations: Iterable[tuple[str, str]]) -> None:
        if not path.is_file():
            return
        text = path.read_text(encoding="utf-8")
        for token, message in expectations:
            if token not in text:
                self.error(category, path, message)

    def _ignored(self, path: Path) -> bool:
        relative_parts = path.resolve().relative_to(self.root).parts
        if any(part in {".git", "bin", "obj", "artifacts", "_site", ".remember", ".superpowers"} for part in relative_parts):
            return True
        return len(relative_parts) >= 2 and relative_parts[0] == "docs" and relative_parts[1] == "api"

    def _has_effective_target_framework(self, project: Path) -> bool:
        """Approximate MSBuild Directory.Build.props inheritance for target frameworks."""
        candidates = [project]
        current = project.parent
        while True:
            props = current / "Directory.Build.props"
            if props.is_file():
                candidates.append(props)
            if current == self.root:
                break
            if self.root not in current.parents:
                break
            current = current.parent

        for candidate in candidates:
            try:
                tree = ET.parse(candidate)
            except Exception:
                continue
            for node_name in ("TargetFramework", "TargetFrameworks"):
                if any((node.text or "").strip() for node in tree.findall(f".//{node_name}")):
                    return True
        return False

    @staticmethod
    def _without_fenced_code(text: str) -> str:
        output: list[str] = []
        in_fence = False
        fence = ""
        for line in text.splitlines():
            stripped = line.lstrip()
            if stripped.startswith(("```", "~~~")):
                marker = stripped[:3]
                if not in_fence:
                    in_fence = True
                    fence = marker
                elif marker == fence:
                    in_fence = False
                output.append("")
            elif in_fence:
                output.append("")
            else:
                output.append(line)
        return "\n".join(output)

    @staticmethod
    def _balanced_csharp(text: str) -> str | None:
        stack: list[tuple[str, int]] = []
        pairs = {"}": "{", ")": "(", "]": "["}
        index = 0
        line = 1
        length = len(text)
        while index < length:
            char = text[index]
            if char == "\n":
                line += 1
                index += 1
                continue
            if char == "/" and index + 1 < length and text[index + 1] == "/":
                index = text.find("\n", index + 2)
                if index == -1:
                    break
                continue
            if char == "/" and index + 1 < length and text[index + 1] == "*":
                end = text.find("*/", index + 2)
                if end == -1:
                    return f"unterminated block comment beginning near line {line}"
                line += text.count("\n", index, end + 2)
                index = end + 2
                continue
            if char == "'":
                index += 1
                escaped = False
                while index < length:
                    current = text[index]
                    if current == "\n":
                        line += 1
                    if current == "'" and not escaped:
                        index += 1
                        break
                    escaped = current == "\\" and not escaped
                    if current != "\\":
                        escaped = False
                    index += 1
                else:
                    return f"unterminated character literal near line {line}"
                continue
            # Recognize regular, verbatim, interpolated, and raw strings. We skip
            # interpolation bodies as text because this pass only checks gross balance.
            string_start = index
            prefix = ""
            while string_start < length and text[string_start] in "$@":
                prefix += text[string_start]
                string_start += 1
                if len(prefix) == 2:
                    break
            if string_start < length and text[string_start] == '"':
                quote_count = 1
                while string_start + quote_count < length and text[string_start + quote_count] == '"':
                    quote_count += 1
                if quote_count >= 3:
                    delimiter = '"' * quote_count
                    end = text.find(delimiter, string_start + quote_count)
                    if end == -1:
                        return f"unterminated raw string beginning near line {line}"
                    line += text.count("\n", index, end + quote_count)
                    index = end + quote_count
                    continue
                verbatim = "@" in prefix
                index = string_start + 1
                while index < length:
                    current = text[index]
                    if current == "\n":
                        line += 1
                    if current == '"':
                        if verbatim and index + 1 < length and text[index + 1] == '"':
                            index += 2
                            continue
                        backslashes = 0
                        probe = index - 1
                        while probe >= 0 and text[probe] == "\\":
                            backslashes += 1
                            probe -= 1
                        if verbatim or backslashes % 2 == 0:
                            index += 1
                            break
                    index += 1
                else:
                    return f"unterminated string beginning near line {line}"
                continue
            if char in "{([":
                stack.append((char, line))
            elif char in "})]":
                if not stack or stack[-1][0] != pairs[char]:
                    return f"unexpected '{char}' on line {line}"
                stack.pop()
            index += 1
        if stack:
            char, opening_line = stack[-1]
            return f"unclosed '{char}' from line {opening_line}"
        return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    validator = Validator(args.root)
    validator.run()
    report = validator.report()
    print(report, end="")
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(report, encoding="utf-8")
    return 0 if not validator.findings else 1


if __name__ == "__main__":
    sys.exit(main())
