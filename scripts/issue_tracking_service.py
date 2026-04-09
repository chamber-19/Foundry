"""
Issue tracking service for the Foundry pipeline.

Manages the lifecycle of software issues (create, assign, resolve) with
role-based access control.  Intended to be used by automation scripts and
the Discord bot to coordinate work items across pipeline contributors.

Roles:
  - admin:     full access — create, assign, and resolve any issue
  - developer: can create and assign issues; can resolve issues they created
                or are assigned to
  - viewer:    read-only access — cannot create, assign, or resolve issues

Issue states:
  open → in_progress (on assignment) → resolved

All state is held in-memory.  Callers that need persistence should serialise
the tracker via ``IssueTracker.to_dict()`` and reconstruct it with
``IssueTracker.from_dict()``.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Optional


# ---------------------------------------------------------------------------
# Enumerations
# ---------------------------------------------------------------------------

class UserRole(Enum):
    ADMIN = "admin"
    DEVELOPER = "developer"
    VIEWER = "viewer"


class IssueStatus(Enum):
    OPEN = "open"
    IN_PROGRESS = "in_progress"
    RESOLVED = "resolved"


# ---------------------------------------------------------------------------
# Exceptions
# ---------------------------------------------------------------------------

class IssueTrackerError(Exception):
    """Base class for all issue-tracker errors."""


class PermissionDeniedError(IssueTrackerError):
    """Raised when a user attempts an operation their role does not allow."""


class IssueNotFoundError(IssueTrackerError):
    """Raised when referencing an issue ID that does not exist."""


# ---------------------------------------------------------------------------
# Data classes
# ---------------------------------------------------------------------------

@dataclass
class User:
    username: str
    role: UserRole


@dataclass
class Issue:
    id: str
    title: str
    body: str
    created_by: str
    status: IssueStatus = IssueStatus.OPEN
    assigned_to: Optional[str] = None
    created_at: datetime = field(
        default_factory=lambda: datetime.now(timezone.utc)
    )
    resolved_at: Optional[datetime] = None
    labels: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "title": self.title,
            "body": self.body,
            "created_by": self.created_by,
            "status": self.status.value,
            "assigned_to": self.assigned_to,
            "created_at": self.created_at.isoformat(),
            "resolved_at": self.resolved_at.isoformat() if self.resolved_at else None,
            "labels": list(self.labels),
        }

    @classmethod
    def from_dict(cls, data: dict) -> "Issue":
        issue = cls(
            id=data["id"],
            title=data["title"],
            body=data["body"],
            created_by=data["created_by"],
            status=IssueStatus(data["status"]),
            assigned_to=data.get("assigned_to"),
            created_at=datetime.fromisoformat(data["created_at"]),
            resolved_at=(
                datetime.fromisoformat(data["resolved_at"])
                if data.get("resolved_at")
                else None
            ),
            labels=data.get("labels", []),
        )
        return issue


# ---------------------------------------------------------------------------
# Tracker
# ---------------------------------------------------------------------------

class IssueTracker:
    """In-memory issue tracker with role-based access control."""

    def __init__(self) -> None:
        self._issues: dict[str, Issue] = {}
        self._next_id: int = 1

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def create_issue(
        self,
        user: User,
        title: str,
        body: str,
        labels: Optional[list[str]] = None,
    ) -> Issue:
        """Create a new issue.

        Allowed roles: admin, developer.
        Raises PermissionDeniedError for viewers.
        Raises ValueError when *title* is blank.
        """
        if user.role == UserRole.VIEWER:
            raise PermissionDeniedError(
                f"User '{user.username}' (role='{user.role.value}') "
                "is not permitted to create issues."
            )
        if not title or not title.strip():
            raise ValueError("Issue title cannot be empty.")

        issue = Issue(
            id=self._generate_id(),
            title=title.strip(),
            body=body,
            created_by=user.username,
            labels=list(labels) if labels else [],
        )
        self._issues[issue.id] = issue
        return issue

    def assign_issue(self, user: User, issue_id: str, assignee: str) -> Issue:
        """Assign an issue to *assignee*.

        Allowed roles: admin, developer.
        Raises PermissionDeniedError for viewers.
        Raises ValueError when *assignee* is blank.
        Transitions status from OPEN → IN_PROGRESS automatically.
        """
        issue = self._get_issue(issue_id)

        if user.role == UserRole.VIEWER:
            raise PermissionDeniedError(
                f"User '{user.username}' (role='{user.role.value}') "
                "is not permitted to assign issues."
            )
        if not assignee or not assignee.strip():
            raise ValueError("Assignee username cannot be empty.")

        issue.assigned_to = assignee.strip()
        if issue.status == IssueStatus.OPEN:
            issue.status = IssueStatus.IN_PROGRESS
        return issue

    def resolve_issue(self, user: User, issue_id: str) -> Issue:
        """Mark an issue as resolved.

        Allowed roles:
          - admin: can resolve any issue.
          - developer: can resolve issues they created or are assigned to.
          - viewer: never allowed (PermissionDeniedError).
        Raises IssueTrackerError when the issue is already resolved.
        """
        issue = self._get_issue(issue_id)

        if user.role == UserRole.VIEWER:
            raise PermissionDeniedError(
                f"User '{user.username}' (role='{user.role.value}') "
                "is not permitted to resolve issues."
            )

        if user.role == UserRole.DEVELOPER:
            if (
                issue.created_by != user.username
                and issue.assigned_to != user.username
            ):
                raise PermissionDeniedError(
                    f"Developer '{user.username}' can only resolve issues "
                    "they created or are assigned to."
                )

        if issue.status == IssueStatus.RESOLVED:
            raise IssueTrackerError(f"Issue '{issue_id}' is already resolved.")

        issue.status = IssueStatus.RESOLVED
        issue.resolved_at = datetime.now(timezone.utc)
        return issue

    def get_issue(self, issue_id: str) -> Issue:
        """Return the issue with *issue_id*.  Raises IssueNotFoundError if absent."""
        return self._get_issue(issue_id)

    def list_issues(self, status: Optional[IssueStatus] = None) -> list[Issue]:
        """Return all issues, optionally filtered by *status*."""
        issues = list(self._issues.values())
        if status is not None:
            issues = [i for i in issues if i.status == status]
        return issues

    def to_dict(self) -> dict:
        return {
            "next_id": self._next_id,
            "issues": {k: v.to_dict() for k, v in self._issues.items()},
        }

    @classmethod
    def from_dict(cls, data: dict) -> "IssueTracker":
        tracker = cls()
        tracker._next_id = data.get("next_id", 1)
        for issue_data in data.get("issues", {}).values():
            issue = Issue.from_dict(issue_data)
            tracker._issues[issue.id] = issue
        return tracker

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _generate_id(self) -> str:
        issue_id = str(self._next_id)
        self._next_id += 1
        return issue_id

    def _get_issue(self, issue_id: str) -> Issue:
        issue = self._issues.get(issue_id)
        if issue is None:
            raise IssueNotFoundError(f"Issue '{issue_id}' not found.")
        return issue
