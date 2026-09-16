# Trim trailing silence instead of padding to maxHops

Snapshots trim trailing silent hops to the last responding hop so text, HTML, CSV, and JSON reports grow organically. This deliberately breaks v0.92's padding of unreached routes to maxHops (30 blank `No response.` rows) because the filler hid the real path length; intermittent silence is still kept and the hop-limit note still explains an unconfirmed destination.
