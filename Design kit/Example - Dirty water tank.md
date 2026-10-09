# Dirty water tank

Example functional description, written from the template. The design the AI made from it is
[dirty-water.design.yaml](../Documentation/Design/dirty-water.design.yaml).

## 1. Purpose

All waste water on board is collected in the dirty water tank. A transfer pump empties the tank automatically.

## 2. Equipment

- Dirty water tank, in the bilge.
- Transfer pump: fixed-speed pump on a contactor in the motor control centre.
- Start float: float switch with large hysteresis. It switches on at the start level and only switches off near the
  bottom of the tank.
- Overfull float: float switch with small hysteresis, mounted above the start level.

## 3. Signals

| Equipment | Signal | Type | Contact |
|---|---|---|---|
| Transfer pump | Running feedback | digital input | normally open |
| Transfer pump | Thermal overload | digital input | normally open |
| Transfer pump | Local/remote selector in remote | digital input | normally open |
| Transfer pump | Run command | digital output | |
| Start float | Level reached | digital input | normally open |
| Overfull float | Tank overfull | digital input | normally closed (a broken wire reads as overfull) |

## 4. Normal operation

In automatic, the pump starts when the start float switches on and stops when it switches off again. The overfull
float also starts the pump, as a backup for a broken start float. In manual the operator starts and stops the pump from
the screen. With the selector in local, the pump is operated at the motor control centre only.

## 5. Faults and alarms

- Tank overfull: alarm after 5 seconds. Can be caused by a broken start float or a broken pump.
- Tank not emptied in time: the pump has run for 10 minutes and the start float is still on.
- Overfull float on while the start float is off: the start float is probably stuck (warning).
- Pump does not start, does not stop, stops unexpectedly, overload: alarm; the tank goes to fault and needs a reset.
- Pump in local: caution.

## 6. Interlocks

None.

## 7. Settings

- Maximum pump-down time: 10 minutes.
- Start the pump on the overfull float: yes, can be switched off.

## 8. Open points

- Is the pump dry-run proof? There is no low-level float.
