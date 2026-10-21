# Advanced backup schedule



The main workspace shows the next estimated execution and time zone. The gear opens a separate modal with an enabled checkbox, Full/Differential/Log intervals, start date/time, explicit Windows time zone, time window, weekday and monthly filters, Run missed backups, preview and a typical weekly-plan preset. Save & Close applies only the current draft; Save Job persists/enables it. Cancel discards dialog edits.

## Calculation rules

- Full is required: 1–525600 minutes. Differential and Log are optional, each 1–525600 minutes when enabled. The sample preset is weekly Full, daily Differential and 15-minute Log; it does not enable itself on existing jobs.
- Intervals are measured in local wall-clock minutes from the shared first-start anchor. Filters omit occurrences without moving the anchor. At least one weekday is required. Month days are 1–31; nonexistent dates are skipped, not clamped.
- A time window is start-inclusive/end-exclusive; crossing midnight is supported. Weekday/month-day eligibility refers to the occurrence's own local date.
- Spring DST nonexistent local times are skipped. Repeated autumn times execute once, at the earlier UTC occurrence. Windows time-zone rules on the service host govern calculation.
- At identical instants Full wins over Differential, which wins over Log. The estimated preview uses the same calculator and suppresses lower-priority collisions.
- The service checks every five seconds. With catch-up enabled, missed work coalesces into one latest eligible occurrence of the highest-priority missed type (Full before Differential before Log), not one execution per missed interval.
- With catch-up disabled, occurrences older than a 90-second polling/startup grace are skipped. Active-job deferrals are reconsidered on the next tick under that same policy.
- Schedule cursors and occurrence keys are durable. A revision change reinitializes scheduling for that revision; with catch-up enabled this may create one recovery-base catch-up. Queued snapshots retain their old revision/configuration. The preview is an estimate, not a promise of available capacity or SQL success.
- Preview search is bounded to five years. An incompatible set of filters can yield no next occurrence.

## Execution and safety

Scheduled Full backups always establish a regular Differential base. The copy-only option applies to manual Full. Save checks enabled backup types against selected database permissions/recovery models; execution checks actual base/Log prerequisites again. `master` supports only Full. No recovery model is changed automatically.

All work is serialized in this service and SQL session application locks protect databases across cooperating hosts. There is no automatic backup-command retry. Only read-only preflight inspection retries recognized transient connection/timeout failures, for at most three attempts with one/two-second delays. Runs interrupted by shutdown remain Interrupted with uncertain stages visible.

## Destination scope

Local/UNC/NAS primary and one optional secondary copy, source mapping and reviewed dependency-aware retention are supported with separate share credentials. Additional providers and unattended retention deletion remain planned.

