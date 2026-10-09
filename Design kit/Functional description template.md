# <System name>

Copy this file, one per system (for example `Dirty water tank.md`), and write it in plain language. Tables are
welcome but not required. The AI asks about everything that is missing, so an incomplete first version is fine.

## 1. Purpose (mandatory)

What the system does and why, in a few sentences.

## 2. Equipment (mandatory)

Every piece of equipment the automation sees or drives: pumps, valves, breakers, sensors, switches.
Per item: its name, what it is, and where it sits in the system.

## 3. Signals (mandatory)

Every input and output between the equipment and the PLC.
Per signal: equipment, what it means, digital or analog, for a contact normally open or normally closed, for an
analog signal its range and unit.

## 4. Normal operation (mandatory)

How the system runs: what starts and stops what, in which order, and on which condition.
Automatic and manual: what changes in manual? Can the operator start and stop equipment from the screen? Locally?

## 5. Faults and alarms (mandatory)

What can go wrong and what must happen then: which alarm the operator gets, how urgent (caution, warning, alarm),
after which delay, and what the automation does (stop, start a backup, nothing).

## 6. Interlocks (when there are any)

Conditions that block starting or stopping equipment, or that stop it (trip). Also conditions on other systems.

## 7. Settings (optional)

Times, limits and options that must be adjustable, with their values when known.

## 8. Open points (optional)

What is not decided yet.
