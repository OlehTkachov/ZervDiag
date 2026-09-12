# CraneCAN 0.7 — Passive CAN Network Discovery

## 1. Purpose

Add a passive network-discovery layer to CraneCAN that builds a logical map of CAN participants and their observed communication without transmitting any CAN frames.

This feature must work with:

- live PCAN receive-only capture;
- Replay TRC;
- previously saved incident/experiment captures;
- unknown machines;
- mixed proprietary/J1939/CANopen traffic.

Important limitation: passive CAN traffic can reveal logical participants, identifiers, directions, periodicity and protocol evidence, but cannot reliably reconstruct the physical wiring topology (actual branch/stub layout, termination placement, cable route) without schematics and/or electrical measurements.

## 2. Safety and operating rule

CraneCAN remains receive-only.

The feature MUST NOT:

- transmit J1939 Request or Address Claim frames;
- transmit CANopen NMT, SDO, PDO, Heartbeat or Node Guarding requests;
- claim a J1939 source address;
- reset or configure a CANopen node;
- perform active discovery or polling;
- inject test traffic into a machine CAN bus.

All discovery must be based on frames already present on the bus.

## 3. Protocol detection

CraneCAN shall classify the observed bus using evidence, not a single identifier.

Suggested states:

- UNKNOWN
- CLASSICAL_CAN_GENERIC
- J1939_LIKELY
- CANOPEN_LIKELY
- MIXED_OR_GATEWAY

Each classification shall have a confidence and evidence list.

### 3.1 J1939 evidence

For every 29-bit frame decode:

- Priority
- EDP/DP
- PF
- PS
- PGN
- Source Address (SA)
- Destination Address (DA) for PDU1
- PDU1/PDU2

Strong J1939 evidence includes:

- Address Claimed PGN 60928 / 0xEE00;
- Request PGN 59904 / 0xEA00;
- coherent SA usage across multiple PGNs;
- repeated PDU1 directed traffic;
- repeated PDU2 broadcast traffic;
- other recognizable J1939 network-management/diagnostic patterns.

Do not label a bus as confirmed J1939 merely because an Extended ID can mathematically be split into PGN/SA fields.

### 3.2 J1939 Address Claim

If PGN 60928 is observed, CraneCAN shall:

- identify the claimed SA from the CAN identifier;
- store the 8-byte NAME payload;
- show first/last claim time;
- detect multiple NAME values claiming the same SA;
- detect Address Cannot Claim when SA = 0xFE;
- preserve raw NAME bytes even if the individual NAME subfields are not yet fully decoded.

The analyzer should later support optional decoding of J1939 NAME fields:

- Identity Number
- Manufacturer Code
- ECU Instance
- Function Instance
- Function
- Vehicle System
- Vehicle System Instance
- Industry Group
- Arbitrary Address Capable

Address-claim information is evidence of node identity. Absence of Address Claim in a short trace does not mean that the node does not exist.

## 4. CANopen detection

CraneCAN shall recognize the CANopen predefined connection set conservatively.

Strong evidence:

- NMT CAN-ID 0x000;
- Heartbeat / Boot-up CAN-ID 0x700 + Node-ID;
- Boot-up payload 0x00;
- Heartbeat state byte:
  - 0x04 Stopped
  - 0x05 Operational
  - 0x7F Pre-operational
- repeated heartbeat period from the same Node-ID.

Additional evidence:

- EMCY 0x080 + Node-ID;
- SDO response/request default ranges;
- PDO ranges from the predefined connection set;
- SYNC 0x080;
- consistent Node-ID reuse across multiple CANopen communication objects.

Because PDO/SDO COB-IDs can be remapped, default ranges alone must not be treated as proof.

For every probable CANopen node show:

- Node-ID
- current observed NMT state
- heartbeat period
- first seen / last seen
- boot-up observed yes/no
- EMCY observed yes/no
- observed COB-IDs
- confidence/evidence.

## 5. Logical Network Map

Add a new tab:

`Сеть / узлы`

The tab must show a logical map, not a physical wiring diagram.

### 5.1 Node table

Columns:

- Protocol
- Node / SA
- Identity
- State
- First seen
- Last seen
- Frames
- Average frequency
- Periodicity quality
- PGN/COB-ID count
- Directed peers
- Health
- Confidence
- Evidence

Examples:

- `J1939 | SA 0x20 | NAME ... | active | ...`
- `CANopen | Node 15 | Operational | HB 1000 ms | ...`
- `Generic | Ext IDs from SA-like suffix 0x21 | unconfirmed | ...`

### 5.2 Communication paths

For J1939 show observed logical edges:

- `SA 0x20 -> DA 0x21`
- `SA 0x21 -> Global/PDU2`

For each edge:

- candidate count
- distinct PGNs
- frame count
- first/last activity
- first change after experiment action
- rate/frequency

Do not infer that an observed logical edge means a direct physical cable connection.

### 5.3 Node details

Selecting a node shall show:

- all PGNs or COB-IDs produced by the node;
- all directed peers;
- periodic messages;
- event-driven messages;
- Address Claim / NAME if available;
- CANopen heartbeat/NMT state if available;
- disappearing/reappearing messages;
- incident links and candidate signals already stored in Machine Profile.

## 6. Node Health / Missing Node

Reuse and generalize the existing passive Node Health logic.

### 6.1 Periodic-message learning

A message may be treated as periodic only after sufficient observation.

Recommended minimum evidence:

- at least 10 s observation where practical;
- enough frames to estimate period;
- acceptable period jitter;
- repeated presence across the training interval.

For each periodic stream store:

- median/average period;
- min/max period;
- jitter;
- last seen;
- expected next frame;
- timeout threshold.

### 6.2 CANopen heartbeat

CANopen heartbeat has strong semantic meaning. A missing heartbeat can be reported as:

`CANopen Node XX heartbeat missing`

The observed heartbeat period should be learned passively. Suggested timeout: configurable, default approximately 3 observed periods, with a minimum floor to prevent false alarms.

Boot-up during normal runtime should be shown as an important event because it can indicate a node reset or power interruption.

### 6.3 J1939 node disappearance

J1939 does not provide a universal heartbeat equivalent for every ECU. Therefore a J1939 node shall be considered missing only when previously learned periodic traffic from that SA stops.

Do not declare a node missing based only on disappearance of an event-driven PGN.

Node states:

- LEARNING
- ACTIVE
- SUSPECT
- MISSING
- RETURNED
- RESET/BOOT OBSERVED
- UNKNOWN

## 7. Protocol-aware candidate analysis

Extend the current candidate table with node/network context.

For every candidate show or make available:

- PGN/COB-ID
- SA
- DA
- protocol estimate
- node identity if known
- node state
- whether the message is periodic or event-driven
- whether it changed before ACTION
- whether it belongs to an address-directed exchange
- whether it belongs to a node that disappeared/restarted.

Candidates from a node that changes before ACTION should be penalized when looking for a causal response.

Candidates from a newly missing/restarted node should be promoted for fault-source experiments.

## 8. Automatic event detection

Network events to record on a timeline:

- new SA / Node-ID appeared;
- SA / Node-ID disappeared;
- Address Claim observed;
- J1939 address conflict observed;
- Address Cannot Claim observed;
- CANopen Boot-up observed;
- CANopen NMT state changed;
- heartbeat lost;
- heartbeat returned;
- new PGN/COB-ID appeared;
- periodic PGN/COB-ID disappeared;
- directed communication path appeared/disappeared.

These events should be usable by Incident Event Chain and First Changes.

## 9. Integration with Machine Profile

Extend `.craneprofile` without breaking existing profiles.

Suggested new optional section:

```json
"network": {
  "protocolEstimate": "J1939_LIKELY",
  "confidence": 0.92,
  "nodes": [],
  "flows": [],
  "evidence": []
}
```

Node evidence must distinguish:

- observed automatically;
- inferred;
- confirmed by documentation;
- confirmed by user;
- rejected.

Do not overwrite manually confirmed machine-profile data with a later automatic guess.

## 10. Network snapshot

Add passive network snapshot export, e.g. `.cannetwork` or extend `.cansnapshot`.

Snapshot should contain:

- capture time/window;
- bitrate if known;
- protocol estimate;
- node list;
- SA/Node-ID health;
- J1939 NAME evidence;
- CANopen heartbeat states;
- PGN/COB-ID inventory;
- logical flows;
- periodicity statistics;
- missing/suspect nodes;
- source TRC/session reference;
- CraneCAN version/schema.

Allow GOOD vs FAULT network snapshot comparison:

- node disappeared/appeared;
- heartbeat missing;
- SA changed;
- Address Claim changed;
- PGN/COB-ID missing/new;
- periodicity changed;
- directed flow missing/new.

## 11. UI proposal

New tab `Сеть / узлы` with three panels:

1. **Protocol summary**
   - Generic / J1939 likely / CANopen likely / Mixed
   - confidence
   - bitrate
   - frame counts Standard/Extended

2. **Nodes**
   - searchable/sortable table
   - ACTIVE/MISSING/RETURNED state
   - click to inspect node details

3. **Flows / events**
   - SA -> DA / Node interaction
   - event timeline
   - filters by node, PGN/COB-ID, state, time

The existing candidate filters should be reused rather than duplicated.

## 12. Confidence rules

Protocol/node conclusions shall always remain explainable.

Example evidence weighting concept:

### J1939

High:
- Address Claim PGN observed;
- repeated coherent SA + PGN behavior.

Medium:
- PDU1/PDU2 traffic patterns with stable SAs;
- recognizable J1939 periodic traffic.

Low:
- only mathematically plausible 29-bit IDs.

### CANopen

High:
- repeated 0x700 + Node-ID heartbeat with valid NMT states;
- boot-up 0x00 followed by heartbeat.

Medium:
- multiple coherent predefined CANopen COB-IDs sharing one Node-ID.

Low:
- a single CAN-ID happens to fall into a default PDO/SDO range.

## 13. Tests

Required smoke/regression tests:

- J1939 PGN/SA/DA parsing;
- PDU1 destination exclusion from PGN;
- Address Claimed PGN detection;
- Address Cannot Claim SA 0xFE;
- two NAMEs claiming one SA;
- CANopen heartbeat parsing;
- Boot-up parsing;
- NMT state decode;
- heartbeat loss and return;
- periodic generic-ID disappearance;
- event-driven ID must not falsely trigger missing-node state;
- mixed J1939 + proprietary Extended traffic;
- mixed Standard CAN + CANopen-like IDs;
- backward compatibility of existing `.craneprofile` and `.cansnapshot`.

## 14. Implementation order

### Phase A — passive protocol core

- J1939 node parser and Address Claim observer;
- CANopen heartbeat/boot-up observer;
- protocol-confidence engine;
- unit/smoke tests.

### Phase B — logical node map

- node aggregation;
- PGN/COB-ID inventory;
- SA->DA flows;
- new `Сеть / узлы` tab.

### Phase C — health

- periodic-stream learning;
- missing/returned states;
- CANopen heartbeat-specific health;
- integrate existing Node Health trigger.

### Phase D — incidents and comparison

- network events in Incident Event Chain;
- network snapshot GOOD/FAULT comparison;
- profile evidence.

### Phase E — optional future active diagnostics

Not part of CraneCAN 0.7 receive-only mode. Any future active J1939/CANopen requests must be a separate explicitly enabled bench-only subsystem with hard safety controls.

## 15. Source basis

The design is based on:

- ISO 11898 / CAN fundamentals and physical/data-link separation;
- SAE J1939-81 Network Management: source-address management, Address Claim and NAME;
- CAN in Automation (CiA) CANopen communication profile concepts: NMT, Boot-up, Heartbeat, COB-ID and Object Dictionary;
- Kvaser J1939 network-management overview;
- PEAK/Kvaser practical CAN physical-layer and topology guidance.

The implementation must preserve the distinction between what is explicitly observed on the bus and what is only inferred by protocol heuristics.
