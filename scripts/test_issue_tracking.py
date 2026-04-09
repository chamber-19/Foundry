"""
Tests for issue_tracking_service.py.

Covers issue creation, assignment, and resolution under all user role
scenarios (admin, developer, viewer), plus edge cases such as blank titles,
unknown issue IDs, and double-resolution.

Test groups:
  1. IssueCreation       — create_issue() under each role
  2. IssueAssignment     — assign_issue() under each role
  3. IssueResolution     — resolve_issue() under each role and ownership rules
  4. IssueStateWorkflow  — full open → in_progress → resolved lifecycle
  5. IssueQuery          — get_issue() and list_issues()
  6. Serialisation       — to_dict() / from_dict() round-trips
"""

import sys
import unittest
from pathlib import Path

# ---------------------------------------------------------------------------
# Import the module under test
# ---------------------------------------------------------------------------
_SCRIPTS_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCRIPTS_DIR))

from issue_tracking_service import (  # noqa: E402
    IssueNotFoundError,
    IssueStatus,
    IssueTracker,
    IssueTrackerError,
    PermissionDeniedError,
    User,
    UserRole,
)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
def _make_tracker() -> IssueTracker:
    return IssueTracker()


def _admin() -> User:
    return User(username="alice", role=UserRole.ADMIN)


def _developer() -> User:
    return User(username="bob", role=UserRole.DEVELOPER)


def _viewer() -> User:
    return User(username="carol", role=UserRole.VIEWER)


def _other_developer() -> User:
    return User(username="dave", role=UserRole.DEVELOPER)


# ---------------------------------------------------------------------------
# Group 1: Issue creation
# ---------------------------------------------------------------------------
class TestIssueCreation(unittest.TestCase):
    """create_issue() must respect role restrictions and validate inputs."""

    def setUp(self):
        self.tracker = _make_tracker()

    # --- admins ---

    def test_admin_can_create_issue(self):
        issue = self.tracker.create_issue(_admin(), "Fix bug", "Details here")
        self.assertEqual(issue.title, "Fix bug")

    def test_admin_create_sets_created_by(self):
        issue = self.tracker.create_issue(_admin(), "Fix bug", "Details")
        self.assertEqual(issue.created_by, "alice")

    def test_admin_create_sets_status_to_open(self):
        issue = self.tracker.create_issue(_admin(), "Fix bug", "Details")
        self.assertEqual(issue.status, IssueStatus.OPEN)

    def test_admin_create_with_labels(self):
        issue = self.tracker.create_issue(_admin(), "Fix bug", "Details", labels=["bug", "high"])
        self.assertIn("bug", issue.labels)
        self.assertIn("high", issue.labels)

    def test_admin_create_assigns_sequential_ids(self):
        issue1 = self.tracker.create_issue(_admin(), "First", "")
        issue2 = self.tracker.create_issue(_admin(), "Second", "")
        self.assertNotEqual(issue1.id, issue2.id)

    # --- developers ---

    def test_developer_can_create_issue(self):
        issue = self.tracker.create_issue(_developer(), "Add feature", "Details")
        self.assertEqual(issue.title, "Add feature")

    def test_developer_create_sets_created_by(self):
        issue = self.tracker.create_issue(_developer(), "Add feature", "Details")
        self.assertEqual(issue.created_by, "bob")

    def test_developer_create_sets_status_to_open(self):
        issue = self.tracker.create_issue(_developer(), "Add feature", "Details")
        self.assertEqual(issue.status, IssueStatus.OPEN)

    # --- viewers ---

    def test_viewer_cannot_create_issue(self):
        with self.assertRaises(PermissionDeniedError):
            self.tracker.create_issue(_viewer(), "Title", "Body")

    def test_viewer_create_error_message_contains_role(self):
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.create_issue(_viewer(), "Title", "Body")
        self.assertIn("viewer", str(ctx.exception))

    def test_viewer_create_error_message_contains_username(self):
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.create_issue(_viewer(), "Title", "Body")
        self.assertIn("carol", str(ctx.exception))

    # --- validation ---

    def test_empty_title_raises_value_error(self):
        with self.assertRaises(ValueError):
            self.tracker.create_issue(_admin(), "", "Body")

    def test_whitespace_only_title_raises_value_error(self):
        with self.assertRaises(ValueError):
            self.tracker.create_issue(_admin(), "   ", "Body")

    def test_title_is_stripped_on_creation(self):
        issue = self.tracker.create_issue(_admin(), "  Trim me  ", "Body")
        self.assertEqual(issue.title, "Trim me")

    def test_issue_id_is_assigned(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.assertIsNotNone(issue.id)
        self.assertNotEqual(issue.id, "")

    def test_created_at_is_set(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.assertIsNotNone(issue.created_at)

    def test_resolved_at_is_none_on_creation(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.assertIsNone(issue.resolved_at)

    def test_assigned_to_is_none_on_creation(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.assertIsNone(issue.assigned_to)

    def test_issue_is_persisted_in_tracker(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        fetched = self.tracker.get_issue(issue.id)
        self.assertEqual(fetched.id, issue.id)


# ---------------------------------------------------------------------------
# Group 2: Issue assignment
# ---------------------------------------------------------------------------
class TestIssueAssignment(unittest.TestCase):
    """assign_issue() must respect role restrictions and update state correctly."""

    def setUp(self):
        self.tracker = _make_tracker()
        self.issue = self.tracker.create_issue(_admin(), "Some issue", "Details")

    # --- admins ---

    def test_admin_can_assign_issue(self):
        self.tracker.assign_issue(_admin(), self.issue.id, "bob")
        self.assertEqual(self.issue.assigned_to, "bob")

    def test_admin_assign_transitions_open_to_in_progress(self):
        self.assertEqual(self.issue.status, IssueStatus.OPEN)
        self.tracker.assign_issue(_admin(), self.issue.id, "bob")
        self.assertEqual(self.issue.status, IssueStatus.IN_PROGRESS)

    def test_admin_assign_to_any_user(self):
        self.tracker.assign_issue(_admin(), self.issue.id, "carol")
        self.assertEqual(self.issue.assigned_to, "carol")

    def test_admin_reassign_does_not_change_status_if_already_in_progress(self):
        self.tracker.assign_issue(_admin(), self.issue.id, "bob")
        self.tracker.assign_issue(_admin(), self.issue.id, "carol")
        self.assertEqual(self.issue.status, IssueStatus.IN_PROGRESS)

    # --- developers ---

    def test_developer_can_assign_issue(self):
        self.tracker.assign_issue(_developer(), self.issue.id, "alice")
        self.assertEqual(self.issue.assigned_to, "alice")

    def test_developer_assign_transitions_open_to_in_progress(self):
        self.tracker.assign_issue(_developer(), self.issue.id, "alice")
        self.assertEqual(self.issue.status, IssueStatus.IN_PROGRESS)

    # --- viewers ---

    def test_viewer_cannot_assign_issue(self):
        with self.assertRaises(PermissionDeniedError):
            self.tracker.assign_issue(_viewer(), self.issue.id, "alice")

    def test_viewer_assign_error_message_contains_role(self):
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.assign_issue(_viewer(), self.issue.id, "alice")
        self.assertIn("viewer", str(ctx.exception))

    def test_viewer_assign_error_message_contains_username(self):
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.assign_issue(_viewer(), self.issue.id, "alice")
        self.assertIn("carol", str(ctx.exception))

    # --- validation ---

    def test_assign_unknown_issue_raises_not_found(self):
        with self.assertRaises(IssueNotFoundError):
            self.tracker.assign_issue(_admin(), "9999", "bob")

    def test_empty_assignee_raises_value_error(self):
        with self.assertRaises(ValueError):
            self.tracker.assign_issue(_admin(), self.issue.id, "")

    def test_whitespace_only_assignee_raises_value_error(self):
        with self.assertRaises(ValueError):
            self.tracker.assign_issue(_admin(), self.issue.id, "   ")

    def test_assignee_is_stripped(self):
        self.tracker.assign_issue(_admin(), self.issue.id, "  bob  ")
        self.assertEqual(self.issue.assigned_to, "bob")

    def test_returned_issue_is_same_object(self):
        returned = self.tracker.assign_issue(_admin(), self.issue.id, "bob")
        self.assertIs(returned, self.issue)


# ---------------------------------------------------------------------------
# Group 3: Issue resolution
# ---------------------------------------------------------------------------
class TestIssueResolution(unittest.TestCase):
    """resolve_issue() must enforce per-role and per-ownership rules."""

    def setUp(self):
        self.tracker = _make_tracker()
        # bob (developer) creates an issue
        self.issue = self.tracker.create_issue(_developer(), "Work item", "Details")
        # Assign to bob
        self.tracker.assign_issue(_admin(), self.issue.id, "bob")

    # --- admins ---

    def test_admin_can_resolve_any_issue(self):
        # Create issue owned by someone else
        other_issue = self.tracker.create_issue(_other_developer(), "Other item", "")
        self.tracker.resolve_issue(_admin(), other_issue.id)
        self.assertEqual(other_issue.status, IssueStatus.RESOLVED)

    def test_admin_resolve_sets_status_resolved(self):
        self.tracker.resolve_issue(_admin(), self.issue.id)
        self.assertEqual(self.issue.status, IssueStatus.RESOLVED)

    def test_admin_resolve_sets_resolved_at(self):
        self.tracker.resolve_issue(_admin(), self.issue.id)
        self.assertIsNotNone(self.issue.resolved_at)

    # --- developers — own issues ---

    def test_developer_can_resolve_issue_they_created(self):
        self.tracker.resolve_issue(_developer(), self.issue.id)
        self.assertEqual(self.issue.status, IssueStatus.RESOLVED)

    def test_developer_can_resolve_issue_they_are_assigned_to(self):
        # dave creates, bob is assignee
        dave_issue = self.tracker.create_issue(_other_developer(), "Dave's item", "")
        self.tracker.assign_issue(_admin(), dave_issue.id, "bob")
        self.tracker.resolve_issue(_developer(), dave_issue.id)
        self.assertEqual(dave_issue.status, IssueStatus.RESOLVED)

    def test_developer_resolve_sets_resolved_at(self):
        self.tracker.resolve_issue(_developer(), self.issue.id)
        self.assertIsNotNone(self.issue.resolved_at)

    # --- developers — other people's issues ---

    def test_developer_cannot_resolve_unrelated_issue(self):
        dave_issue = self.tracker.create_issue(_other_developer(), "Dave's item", "")
        with self.assertRaises(PermissionDeniedError):
            self.tracker.resolve_issue(_developer(), dave_issue.id)

    def test_developer_denied_error_message_contains_username(self):
        dave_issue = self.tracker.create_issue(_other_developer(), "Dave's item", "")
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.resolve_issue(_developer(), dave_issue.id)
        self.assertIn("bob", str(ctx.exception))

    # --- viewers ---

    def test_viewer_cannot_resolve_issue(self):
        with self.assertRaises(PermissionDeniedError):
            self.tracker.resolve_issue(_viewer(), self.issue.id)

    def test_viewer_resolve_error_message_contains_role(self):
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.resolve_issue(_viewer(), self.issue.id)
        self.assertIn("viewer", str(ctx.exception))

    def test_viewer_resolve_error_message_contains_username(self):
        with self.assertRaises(PermissionDeniedError) as ctx:
            self.tracker.resolve_issue(_viewer(), self.issue.id)
        self.assertIn("carol", str(ctx.exception))

    # --- idempotency / double-resolve ---

    def test_resolving_already_resolved_issue_raises_error(self):
        self.tracker.resolve_issue(_admin(), self.issue.id)
        with self.assertRaises(IssueTrackerError):
            self.tracker.resolve_issue(_admin(), self.issue.id)

    def test_resolve_unknown_issue_raises_not_found(self):
        with self.assertRaises(IssueNotFoundError):
            self.tracker.resolve_issue(_admin(), "9999")

    def test_returned_issue_is_same_object(self):
        returned = self.tracker.resolve_issue(_admin(), self.issue.id)
        self.assertIs(returned, self.issue)


# ---------------------------------------------------------------------------
# Group 4: Full state workflow
# ---------------------------------------------------------------------------
class TestIssueStateWorkflow(unittest.TestCase):
    """Validate the open → in_progress → resolved lifecycle transitions."""

    def setUp(self):
        self.tracker = _make_tracker()

    def test_new_issue_is_open(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.assertEqual(issue.status, IssueStatus.OPEN)

    def test_assigning_open_issue_moves_to_in_progress(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.tracker.assign_issue(_admin(), issue.id, "bob")
        self.assertEqual(issue.status, IssueStatus.IN_PROGRESS)

    def test_resolving_in_progress_issue_moves_to_resolved(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.tracker.assign_issue(_admin(), issue.id, "bob")
        self.tracker.resolve_issue(_admin(), issue.id)
        self.assertEqual(issue.status, IssueStatus.RESOLVED)

    def test_resolving_open_issue_directly_is_allowed_for_admin(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.tracker.resolve_issue(_admin(), issue.id)
        self.assertEqual(issue.status, IssueStatus.RESOLVED)

    def test_full_lifecycle_resolved_at_is_set(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.tracker.assign_issue(_admin(), issue.id, "bob")
        self.tracker.resolve_issue(_admin(), issue.id)
        self.assertIsNotNone(issue.resolved_at)

    def test_full_lifecycle_assigned_to_persists_after_resolve(self):
        issue = self.tracker.create_issue(_admin(), "Title", "Body")
        self.tracker.assign_issue(_admin(), issue.id, "bob")
        self.tracker.resolve_issue(_admin(), issue.id)
        self.assertEqual(issue.assigned_to, "bob")


# ---------------------------------------------------------------------------
# Group 5: Issue query
# ---------------------------------------------------------------------------
class TestIssueQuery(unittest.TestCase):
    """get_issue() and list_issues() behave correctly."""

    def setUp(self):
        self.tracker = _make_tracker()
        self.open_issue = self.tracker.create_issue(_admin(), "Open one", "")
        self.in_progress_issue = self.tracker.create_issue(_admin(), "In progress one", "")
        self.tracker.assign_issue(_admin(), self.in_progress_issue.id, "bob")
        self.resolved_issue = self.tracker.create_issue(_admin(), "Resolved one", "")
        self.tracker.resolve_issue(_admin(), self.resolved_issue.id)

    def test_get_issue_returns_correct_issue(self):
        fetched = self.tracker.get_issue(self.open_issue.id)
        self.assertEqual(fetched.id, self.open_issue.id)

    def test_get_unknown_issue_raises_not_found(self):
        with self.assertRaises(IssueNotFoundError):
            self.tracker.get_issue("does-not-exist")

    def test_list_issues_returns_all_when_no_filter(self):
        all_issues = self.tracker.list_issues()
        self.assertEqual(len(all_issues), 3)

    def test_list_issues_filter_by_open(self):
        open_issues = self.tracker.list_issues(status=IssueStatus.OPEN)
        self.assertEqual(len(open_issues), 1)
        self.assertEqual(open_issues[0].id, self.open_issue.id)

    def test_list_issues_filter_by_in_progress(self):
        in_progress = self.tracker.list_issues(status=IssueStatus.IN_PROGRESS)
        self.assertEqual(len(in_progress), 1)
        self.assertEqual(in_progress[0].id, self.in_progress_issue.id)

    def test_list_issues_filter_by_resolved(self):
        resolved = self.tracker.list_issues(status=IssueStatus.RESOLVED)
        self.assertEqual(len(resolved), 1)
        self.assertEqual(resolved[0].id, self.resolved_issue.id)

    def test_list_issues_returns_empty_list_when_none_match(self):
        fresh = _make_tracker()
        self.assertEqual(fresh.list_issues(status=IssueStatus.RESOLVED), [])


# ---------------------------------------------------------------------------
# Group 6: Serialisation round-trips
# ---------------------------------------------------------------------------
class TestSerialisation(unittest.TestCase):
    """to_dict() / from_dict() must faithfully preserve all issue state."""

    def setUp(self):
        self.tracker = _make_tracker()
        self.issue = self.tracker.create_issue(
            _admin(), "Serialise me", "Body text", labels=["test"]
        )
        self.tracker.assign_issue(_admin(), self.issue.id, "bob")

    def test_issue_to_dict_contains_required_keys(self):
        d = self.issue.to_dict()
        expected_keys = {"id", "title", "body", "created_by", "status", "assigned_to",
                         "created_at", "resolved_at", "labels"}
        self.assertEqual(expected_keys, set(d.keys()))

    def test_issue_to_dict_status_is_string(self):
        d = self.issue.to_dict()
        self.assertIsInstance(d["status"], str)

    def test_issue_to_dict_labels_are_list(self):
        d = self.issue.to_dict()
        self.assertIsInstance(d["labels"], list)

    def test_issue_round_trip_preserves_title(self):
        from issue_tracking_service import Issue
        restored = Issue.from_dict(self.issue.to_dict())
        self.assertEqual(restored.title, self.issue.title)

    def test_issue_round_trip_preserves_status(self):
        from issue_tracking_service import Issue
        restored = Issue.from_dict(self.issue.to_dict())
        self.assertEqual(restored.status, self.issue.status)

    def test_issue_round_trip_preserves_assigned_to(self):
        from issue_tracking_service import Issue
        restored = Issue.from_dict(self.issue.to_dict())
        self.assertEqual(restored.assigned_to, "bob")

    def test_tracker_round_trip_preserves_issue_count(self):
        data = self.tracker.to_dict()
        restored = IssueTracker.from_dict(data)
        self.assertEqual(len(restored.list_issues()), 1)

    def test_tracker_round_trip_preserves_next_id(self):
        data = self.tracker.to_dict()
        restored = IssueTracker.from_dict(data)
        new_issue = restored.create_issue(_admin(), "New", "")
        self.assertNotEqual(new_issue.id, self.issue.id)

    def test_resolved_issue_round_trip_preserves_resolved_at(self):
        from issue_tracking_service import Issue
        self.tracker.resolve_issue(_admin(), self.issue.id)
        restored = Issue.from_dict(self.issue.to_dict())
        self.assertIsNotNone(restored.resolved_at)

    def test_unresolved_issue_resolved_at_is_none_after_round_trip(self):
        fresh_issue = self.tracker.create_issue(_admin(), "Fresh", "")
        from issue_tracking_service import Issue
        restored = Issue.from_dict(fresh_issue.to_dict())
        self.assertIsNone(restored.resolved_at)


if __name__ == "__main__":
    unittest.main()
