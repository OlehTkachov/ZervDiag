# J1939 PGN-normalized Incident First Changes

CraneCAN keeps the existing Incident First Changes analyzer as the raw,
exact-CAN-ID evidence view. This feature adds a second, parallel view for
29-bit traffic that is interpreted as J1939.

## Why it exists

A J1939 29-bit CAN identifier contains fields including Priority, PGN and
Source Address. For PDU1 messages, the PS byte is a Destination Address;
for PDU2 it is part of the PGN.

If the transmitting ECU changes Source Address, a raw CAN-ID analyzer sees
one numerical identifier disappear and another numerical identifier appear,
even when the J1939 message type did not change. The PGN-normalized view
prevents that address-only change from being promoted to a message
appearance/disappearance.

The raw exact-ID result remains available and unchanged.

## Normalized message identity

For this analysis only, CraneCAN builds a deterministic internal key:

- Priority is normalized and does not define message identity.
- Source Address is normalized and does not define message identity.
- PGN is retained.
- PDU1 Destination Address is retained.
- Classical CAN payload bytes and timestamps are not changed.

Therefore:

- PDU2: identity is PGN.
- PDU1: identity is PGN + Destination Address.

The normalized identifier is an internal analysis key only. It is not
transmitted, written to the crane, or substituted into the original TRC or
incident package.

## What the view reports

The J1939 PGN First Changes window shows:

- marker-relative reaction time;
- HIGH / MEDIUM / INFO ranking inherited from the established First Changes
  logic;
- PGN;
- PDU1 Destination Address when present;
- DATA byte or PGN/DLC location;
- appeared / stopped / DLC changed / byte changed event;
- baseline and observed values;
- Source Address sets in BASELINE and SEARCH;
- original raw 29-bit CAN IDs seen in both windows;
- baseline agreement and confirmation count.

The summary also reports:

- the number of exact-ID lifecycle candidates suppressed by PGN
  normalization;
- PGN/DA keys whose Source Address set changed between BASELINE and SEARCH;
- 29-bit frame counts in the two windows.

A Source Address set change is metadata. It is not automatically treated as
a fault or as a PGN lifecycle event.

## Multiple Source Addresses

More than one ECU may send the same PGN. When multiple Source Addresses are
present for one PGN/DA key, the PGN-normalized byte/DLC analysis combines
those frames intentionally.

This is useful for detecting message-level continuity across address
changes, but it can make byte statistics ambiguous when different senders
use different payload semantics. CraneCAN emits an explicit warning and the
engineer should compare the raw exact-ID view.

## PDU1 rule

For PDU1 (`PF < 240`) the Destination Address is not part of the PGN itself,
but it is semantically relevant to the addressed message. CraneCAN therefore
keeps Destination Address in the normalized key.

Changing only PDU1 Destination Address is not suppressed.

## Scope and safety

This view interprets ordinary Rx 29-bit Classical CAN frames as J1939.
A 29-bit CAN identifier alone does not prove that the bus uses J1939, so the
raw exact-ID analysis is always preserved for verification.

The analyzer is offline/read-only. It does not add a CAN transmit path and
does not modify `.trc`, `.canincident` or Machine Profile data.

The result is observational evidence only. It does not prove physical
causality or identify a failed ECU automatically.
