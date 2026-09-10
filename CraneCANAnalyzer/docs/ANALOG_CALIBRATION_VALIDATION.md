# Analog Calibration Validation Run

## Purpose

`Calibration Validation Run` checks an already calibrated Machine Profile signal against new physical measurements that were not used to calculate `Scale` and `Offset`.

The validation formula is fixed during the run:

```text
predicted physical = raw * existing Scale + existing Offset
```

The validator never refits the line and never changes `Scale`, `Offset`, `Unit` or `Confidence`.

This stage is intended to answer a narrower engineering question than calibration itself: **does the current CAN-to-physical conversion reproduce independent physical measurements with acceptable error over the range that was actually checked?**

It does not prove sensor identity, system causality, safety function or ECU semantics.

## Operator workflow

1. First create a Machine Profile signal and perform physical calibration with `Физические точки…` or `Калибровка raw…`.
2. Keep the resulting `Scale`, `Offset` and `Unit` unchanged.
3. Open `Проверка калибровки…`.
4. Select the calibrated signal.
5. Move the mechanism to a **new physical position that was not used in the calibration fit**.
6. Measure the actual physical value independently and enter it in the signal's existing Unit.
7. Hold the mechanism stationary and press `Зафиксировать независимую точку`.
8. Repeat for at least three different physical positions, distributed through the working range.
9. Press `Проверить калибровку`.
10. Review measured value, CAN prediction and error for every validation point.
11. Only a `PASS` result enables `Записать PASS evidence`.

The source Machine Profile is not saved automatically. After recording evidence, explicitly save the profile if the result must be kept permanently.

## Passive raw capture

Validation uses the same passive marker capture engine as physical calibration:

- Classical CAN Rx data frames only;
- no Tx path;
- PCAN Live must be in confirmed `LISTEN ONLY` mode;
- Replay uses already recorded TRC data;
- remote/error frames are ignored;
- Standard and Extended IDs are kept separate;
- J1939 signals can match by PGN and are sampled from one current Source Address only;
- signed and unsigned signals are supported;
- LittleEndian and BigEndian/Motorola fields use the Machine Profile definition;
- the raw value is captured before applying the signal's current Scale/Offset.

A marker is calculated from a trailing 750 ms window with at least 5 matching samples. The median raw value is used. Robust span and drift are checked; a moving/unstable signal is rejected and the point is not added.

For PCAN Live, the latest matching frame must be fresh (maximum age 2 seconds). Replay timestamps are not compared with the wall clock.

## Independent validation requirements

The Core validator requires:

- at least 3 validation points;
- at least 3 distinct raw values;
- at least 3 distinct measured physical values;
- a non-empty engineering `Unit` already stored in the signal;
- finite raw, physical, Scale and Offset values.

The software cannot prove that a validation point was not previously used for calibration. Independence is therefore an operator responsibility and is stated explicitly in both the UI and recorded evidence.

## Error metrics

For every point:

```text
error = predicted physical - measured physical
```

The result contains:

- mean error (bias);
- RMSE;
- maximum absolute error;
- measured validation span;
- RMSE as percent of validation span;
- maximum absolute error as percent of validation span;
- a row-by-row measured/predicted/error table.

The percentages are normalized to the physical span covered by the validation points. The result therefore applies only to that checked span. It does not validate extrapolation outside it.

## Result classification

Current thresholds are intentionally explicit and deterministic:

```text
PASS:
  RMSE <= 1.5% of validation span
  AND max absolute error <= 2.5% of validation span

CAUTION:
  RMSE <= 3.0% of validation span
  AND max absolute error <= 5.0% of validation span
  but PASS thresholds are not met

FAIL:
  anything worse
```

`CAUTION` and `FAIL` block validation evidence recording. They do not modify the profile.

These are generic analyzer thresholds, not manufacturer tolerances. If an OEM or component specification defines a stricter permissible error, that specification remains authoritative.

## PASS evidence

A successful `PASS` can append one `PhysicalOutputCheck` evidence record with capture origin:

```text
analog-calibration-validation
```

The evidence records:

- SignalId;
- evaluated Scale and Offset;
- Unit;
- validation point count;
- measured/predicted/error values;
- mean error;
- RMSE;
- maximum error;
- checked physical span;
- the fact that existing Scale/Offset were evaluated without refitting;
- the requirement that the operator used independent points;
- the statement that validation does not establish causality and does not change Confidence automatically.

Before evidence is appended, the Core verifies that `SignalId`, `Scale`, `Offset` and `Unit` still match the values that were validated. If the calibration changed after the run, the old result is rejected as stale and a new Validation Run is required.

## Safety and interpretation

This feature is read-only with respect to the CAN bus. It never sends CAN frames or service commands.

A PASS means only that the configured conversion reproduced the independent measurements within the generic thresholds over the measured span. It is supporting evidence for engineering review, not an automatic `CONFIRMED` decision.
