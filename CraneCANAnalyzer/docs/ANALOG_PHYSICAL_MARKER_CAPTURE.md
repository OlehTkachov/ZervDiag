# Analog Physical Marker Capture

## Purpose

Physical Marker Capture removes manual transcription of raw CAN values during analog sensor calibration. An engineer selects an existing `MachineSignal`, places the mechanism at a physically measured position, enters the measured value and unit, and presses **«Зафиксировать точку из Live/Replay»**. CraneCAN reads only frames already received into the Live/Replay buffer and records a robust raw marker for later linear calibration.

This feature is diagnostic and passive. It does not transmit CAN frames, request ECU data, write parameters, reset sensors, or change a machine controller.

## Operator workflow

1. Create or open a Machine Profile containing the raw signal to calibrate. A signal may come from Guided Analog Search, DBC/J1939 import, or another independently verified source.
2. Start a PEAK PCAN-USB Live receive session in confirmed **LISTEN ONLY**, or run a TRC Replay so its frames are present in the Live buffer.
3. Open **«Физические точки…»** and select the signal.
4. Enter the engineering unit explicitly, for example `m`, `deg`, `bar`, `MPa`, `mm`.
5. Move the mechanism to a known physical position, stop movement, and hold the position.
6. Enter the independently measured physical value, for example `12.5`.
7. Press **«Зафиксировать точку из Live/Replay»**.
8. Repeat at another position. Two points can calculate Scale/Offset; three or more well-spaced points are recommended to evaluate linearity.
9. Press **«Рассчитать»** and review R², RMSE, maximum error, residuals and quality.
10. Apply Scale/Offset/Unit only after the measurements are acceptable. Saving the Machine Profile to disk remains a separate explicit action.

## Raw value used for a marker

The marker uses the decoded **raw** value before the signal's current Scale and Offset. This is important when recalibrating a signal that already has engineering scaling.

Signed signals use the signed raw value. LittleEndian and DBC/Motorola BigEndian definitions use the same `MachineSignalDecoder` conventions as the rest of CraneCAN.

## Sampling and stability gate

A marker is not based on a single frame. The current implementation examines a trailing **750 ms** sampling window and requires at least **5** matching Rx samples.

For the selected sender, CraneCAN calculates:

- median raw value — stored as the calibration marker;
- raw minimum and maximum;
- robust 5th–95th percentile span;
- drift between the first and last halves of the sampling window;
- a stability limit derived from signal full scale and clamped to a field-practical raw range.

Default stability tolerance is `0.1%` of raw full scale, clamped to **1…64 raw counts**. Both robust span and drift must be within that limit. If the signal is still moving, the result is shown as `UNSTABLE` and the point is **not added** to the calibration set.

This gate is deliberately conservative about recording a physical marker while a boom, cylinder, angle sensor or pressure signal is still changing. It does not prove that a stable CAN value is the intended physical sensor.

## PCAN Live freshness

For real PCAN Live capture, the last matching frame must also be recent. The UI currently requires a matching frame no older than **2 seconds** relative to the capture action. If the frame is stale, marker creation is blocked and the engineer is asked to check the Live CAN stream.

The PCAN path still requires the existing hardware-confirmed **LISTEN ONLY** connection. Physical Marker Capture adds no transmit capability.

## Replay behavior

TRC Replay has recorded timestamps that are intentionally unrelated to current wall-clock time. Therefore wall-clock freshness is not applied to Replay. Marker capture uses the current/last frames present in that Replay receiver buffer and still applies the same sample-count and stability tests.

This makes it possible to validate the marker and calibration workflow without a connected machine.

## J1939 behavior

For a Machine Profile signal with explicit J1939 PGN metadata, matching follows the existing J1939 signal rules:

- same PGN can match when Source Address changes;
- for PDU1, Destination Address must still match the signal definition;
- Standard and Extended CAN are never merged.

A special protection is applied when several ECUs publish the same PGN. CraneCAN first determines the **Source Address of the latest matching frame**, then uses only frames from that Source Address for the marker sampling window. Values from two simultaneous J1939 senders are therefore not averaged into one physical marker.

The result records PGN, current Source Address, optional PDU1 Destination Address and the observed raw CAN ID for engineering review.

## Calibration after marker capture

The captured pairs are passed to the same `AnalogPhysicalCalibration` implementation used by manual raw calibration:

`physical = raw × Scale + Offset`

Two points always define a straight line and therefore cannot independently validate sensor linearity. With three or more points CraneCAN evaluates R², RMSE and maximum error. A `POOR` calibration is blocked from being written into the Machine Profile.

The physical value and engineering unit are entered by the engineer. CraneCAN does not infer that a field is metres, degrees, pressure, load, current, or any other physical quantity solely from CAN behavior.

## Machine Profile and evidence

Applying the result updates Scale, Offset and Unit and appends the existing `PhysicalOutputCheck` calibration evidence. The signal's `Confidence` is intentionally preserved. A CANDIDATE does not become PROBABLE or CONFIRMED merely because a linear scale was calculated.

The profile is not saved to disk automatically. The operator must explicitly save the Machine Profile after reviewing the result.

## Safety / interpretation limits

A stable and linear CAN signal is evidence that the decoded field tracks the supplied physical measurements. It is not, by itself, proof of sensor identity, ECU semantics, safety function, or causality.

Physical Marker Capture is read-only with respect to the CAN bus. There is no Tx operation in this feature.
