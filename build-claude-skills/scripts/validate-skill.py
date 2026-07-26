#!/usr/bin/env python3
"""
Skill Validator: Verify that a Claude Skill follows best practices.

Usage:
    python3 validate-skill.py /path/to/skill-folder
    python3 validate-skill.py .  # Validate current directory
"""

import os
import sys
import re
import yaml
from pathlib import Path


class SkillValidator:
    def __init__(self, skill_path):
        self.skill_path = Path(skill_path).resolve()
        self.errors = []
        self.warnings = []
        self.info = []
        self.skill_name = self.skill_path.name

    def validate(self):
        """Run all validation checks."""
        print(f"\n🔍 Validating Skill: {self.skill_name}\n")

        # Check folder structure
        self.check_folder_exists()
        self.check_skill_md_exists()
        self.check_folder_naming()

        # Check SKILL.md content
        if self.skill_path.joinpath("SKILL.md").exists():
            self.check_yaml_frontmatter()
            self.check_skill_content()
            self.check_description_quality()

        # Check optional folders
        self.check_optional_structure()

        # Report results
        self.print_results()
        return len(self.errors) == 0

    def check_folder_exists(self):
        """Verify skill folder exists."""
        if not self.skill_path.exists():
            self.errors.append(f"Skill folder does not exist: {self.skill_path}")
        elif not self.skill_path.is_dir():
            self.errors.append(f"Skill path is not a directory: {self.skill_path}")

    def check_skill_md_exists(self):
        """Verify SKILL.md exists with correct case."""
        skill_md = self.skill_path / "SKILL.md"
        if not skill_md.exists():
            # Check for incorrect case variations
            for file in self.skill_path.iterdir():
                if file.name.lower() == "skill.md":
                    self.errors.append(
                        f"File found but with wrong case: {file.name} (must be exactly 'SKILL.md')"
                    )
                    return
            self.errors.append("SKILL.md not found (required)")

    def check_folder_naming(self):
        """Verify folder name is kebab-case."""
        if not re.match(r"^[a-z0-9]+(-[a-z0-9]+)*$", self.skill_name):
            self.errors.append(
                f"Folder name '{self.skill_name}' is not kebab-case. Use lowercase letters, numbers, and hyphens only."
            )
        if "claude" in self.skill_name or "anthropic" in self.skill_name:
            self.errors.append(
                f"Skill name '{self.skill_name}' contains reserved words 'claude' or 'anthropic'"
            )

    def check_yaml_frontmatter(self):
        """Validate YAML frontmatter structure."""
        skill_md_path = self.skill_path / "SKILL.md"

        try:
            with open(skill_md_path, "r") as f:
                content = f.read()

            # Check for YAML delimiters
            if not content.startswith("---"):
                self.errors.append("SKILL.md must start with '---' (YAML delimiter)")
                return

            # Extract YAML content
            parts = content.split("---", 2)
            if len(parts) < 3:
                self.errors.append(
                    "SKILL.md must have closing '---' after YAML frontmatter"
                )
                return

            yaml_content = parts[1]

            # Parse YAML
            try:
                frontmatter = yaml.safe_load(yaml_content)
            except yaml.YAMLError as e:
                self.errors.append(f"Invalid YAML syntax in frontmatter: {e}")
                return

            # Check required fields
            if not frontmatter or not isinstance(frontmatter, dict):
                self.errors.append("YAML frontmatter must be a dictionary with name and description")
                return

            if "name" not in frontmatter:
                self.errors.append("Missing required field: 'name' in YAML frontmatter")
            else:
                self.check_name_field(frontmatter["name"])

            if "description" not in frontmatter:
                self.errors.append("Missing required field: 'description' in YAML frontmatter")
            else:
                self.check_description_field(frontmatter["description"])

            # Check for forbidden content
            if "<" in yaml_content or ">" in yaml_content:
                self.warnings.append("YAML frontmatter contains XML tags (< >). This may cause issues.")

            # Store frontmatter for later checks
            self.frontmatter = frontmatter

        except Exception as e:
            self.errors.append(f"Error reading SKILL.md: {e}")

    def check_name_field(self, name):
        """Validate the 'name' field."""
        if not isinstance(name, str):
            self.errors.append(f"'name' must be a string, got {type(name).__name__}")
            return

        if not re.match(r"^[a-z0-9]+(-[a-z0-9]+)*$", name):
            self.errors.append(
                f"'name' field '{name}' is not kebab-case. Use lowercase letters, numbers, and hyphens only."
            )

        if name != self.skill_name:
            self.warnings.append(
                f"Skill folder name '{self.skill_name}' does not match 'name' field '{name}'"
            )

    def check_description_field(self, description):
        """Validate the 'description' field."""
        if not isinstance(description, str):
            self.errors.append(f"'description' must be a string, got {type(description).__name__}")
            return

        if len(description) > 1024:
            self.errors.append(f"'description' exceeds 1024 characters ({len(description)} chars)")

        if len(description) < 20:
            self.warnings.append("'description' seems too short to be useful")

        # Check for common issues
        if description.lower() == description.lower().strip():
            if "when" not in description.lower() and "use" not in description.lower():
                self.warnings.append(
                    "'description' should include when/how to use the skill (trigger phrases)"
                )

    def check_skill_content(self):
        """Check SKILL.md content structure."""
        skill_md_path = self.skill_path / "SKILL.md"

        try:
            with open(skill_md_path, "r") as f:
                content = f.read()

            # Check for section headers
            sections = {
                "## Instructions": "Instructions section",
                "## Examples": "Examples section",
                "## Troubleshooting": "Troubleshooting section",
            }

            found_sections = {header: header in content for header, _ in sections.items()}

            if not found_sections["## Instructions"]:
                self.warnings.append("Missing '## Instructions' section (recommended)")
            if not found_sections["## Examples"]:
                self.warnings.append("Missing '## Examples' section (recommended)")
            if not found_sections["## Troubleshooting"]:
                self.warnings.append("Missing '## Troubleshooting' section (recommended)")

            # Check for broken markdown links
            links = re.findall(r"\[([^\]]+)\]\(([^)]+)\)", content)
            for link_text, link_path in links:
                if not link_path.startswith("http"):
                    full_path = self.skill_path / link_path
                    if not full_path.exists():
                        self.warnings.append(f"Broken link: {link_path} (file not found)")

        except Exception as e:
            self.errors.append(f"Error reading SKILL.md content: {e}")

    def check_description_quality(self):
        """Check description quality and specificity."""
        if not hasattr(self, "frontmatter"):
            return

        description = self.frontmatter.get("description", "").lower()

        # Check for vague descriptions
        vague_phrases = ["helps with", "does things", "handles stuff", "process data"]
        for phrase in vague_phrases:
            if phrase in description:
                self.warnings.append(f"Description contains vague phrase: '{phrase}'")

        # Check if it includes trigger phrases
        has_trigger_phrases = any(
            word in description
            for word in ["use when", "use for", "when user", "if you", "trigger"]
        )
        if not has_trigger_phrases:
            self.warnings.append(
                "Description should include trigger phrases (e.g., 'Use when user...')"
            )

    def check_optional_structure(self):
        """Check optional folders and files."""
        optional_dirs = ["scripts", "references", "assets"]

        for opt_dir in optional_dirs:
            dir_path = self.skill_path / opt_dir
            if dir_path.exists():
                if not dir_path.is_dir():
                    self.warnings.append(f"'{opt_dir}' exists but is not a directory")
                else:
                    file_count = len(list(dir_path.iterdir()))
                    self.info.append(f"Found '{opt_dir}' folder with {file_count} item(s)")

        # Check for README in skill folder (should be at repo level, not skill level)
        readme_path = self.skill_path / "README.md"
        if readme_path.exists():
            self.warnings.append(
                "README.md found inside skill folder. Move to repository root, not inside skill folder."
            )

    def print_results(self):
        """Print validation results."""
        print("=" * 60)
        print(f"Validation Results for: {self.skill_name}")
        print("=" * 60)

        if self.errors:
            print(f"\n❌ ERRORS ({len(self.errors)}):")
            for i, error in enumerate(self.errors, 1):
                print(f"  {i}. {error}")

        if self.warnings:
            print(f"\n⚠️  WARNINGS ({len(self.warnings)}):")
            for i, warning in enumerate(self.warnings, 1):
                print(f"  {i}. {warning}")

        if self.info:
            print(f"\nℹ️  INFO ({len(self.info)}):")
            for i, info in enumerate(self.info, 1):
                print(f"  {i}. {info}")

        print("\n" + "=" * 60)
        if not self.errors:
            print("✅ Skill is valid and ready to use!")
        else:
            print(f"❌ Found {len(self.errors)} error(s) that must be fixed.")
            print("\nTo see detailed guidance, check: references/validation-checklist.md")
        print("=" * 60 + "\n")

        # Return appropriate exit code
        return len(self.errors) == 0


def main():
    """Entry point."""
    if len(sys.argv) > 1:
        skill_path = sys.argv[1]
    else:
        print("Usage: python3 validate-skill.py /path/to/skill-folder")
        print("       python3 validate-skill.py .  # Current directory")
        sys.exit(1)

    validator = SkillValidator(skill_path)
    is_valid = validator.validate()

    sys.exit(0 if is_valid else 1)


if __name__ == "__main__":
    main()
