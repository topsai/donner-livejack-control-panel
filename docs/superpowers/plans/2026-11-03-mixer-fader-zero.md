# Mixer fader zero

Official Banana fader range -60 to+12dB is intentional. A90px linear72dB fader cannot reliably hit exact0 by pointer, and TickFrequency alone did not enable snapping. Keep the range and external readback, add0.1dB tick snapping, pointer-only unity detent within1dB, doubleclick fader reset and click numeric gain reset. Keyboard0.1dB steps remain available nearunity. Test quantization/edgecases and input/output UI reset with fake backend; no real gain writes during verification. Publishv1.29 with refreshed screenshots/docs.
