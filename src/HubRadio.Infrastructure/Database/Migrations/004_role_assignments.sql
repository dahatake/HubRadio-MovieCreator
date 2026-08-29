CREATE TABLE role_assignments (
    actor_sid TEXT NOT NULL CHECK (
        length(actor_sid) > 0
        AND trim(actor_sid) = actor_sid
    ),
    role TEXT NOT NULL CHECK (
        role IN (
            'Viewer',
            'Editor',
            'PerformerReviewer',
            'ContentApprover',
            'BrandApprover',
            'RightsApprover',
            'Administrator'
        )
    ),
    assigned_at TEXT NOT NULL CHECK (
        length(assigned_at) > 0
        AND trim(assigned_at) = assigned_at
    ),
    assigned_by_sid TEXT NOT NULL CHECK (
        length(assigned_by_sid) > 0
        AND trim(assigned_by_sid) = assigned_by_sid
    ),
    reason TEXT NOT NULL CHECK (
        length(reason) > 0
        AND trim(reason) = reason
    ),
    PRIMARY KEY (actor_sid, role)
) STRICT;