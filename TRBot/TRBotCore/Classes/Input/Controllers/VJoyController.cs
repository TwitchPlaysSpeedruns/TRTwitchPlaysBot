﻿using System;
using System.Collections.Generic;
using System.Text;
using vJoyInterfaceWrap;
using System.Runtime.CompilerServices;
using static vJoyInterfaceWrap.vJoy;

namespace TRBot
{
    public class VJoyController : IVirtualController
    {
        /// <summary>
        /// The mapping from axis number to axis code.
        /// </summary>
        private static readonly Dictionary<int, int> AxisCodeMap = new Dictionary<int, int>(8)
        {
            { (int)GlobalAxisVals.AXIS_X,  (int)HID_USAGES.HID_USAGE_X },
            { (int)GlobalAxisVals.AXIS_Y,  (int)HID_USAGES.HID_USAGE_Y },
            { (int)GlobalAxisVals.AXIS_Z,  (int)HID_USAGES.HID_USAGE_Z },
            { (int)GlobalAxisVals.AXIS_RX, (int)HID_USAGES.HID_USAGE_RX },
            { (int)GlobalAxisVals.AXIS_RY, (int)HID_USAGES.HID_USAGE_RY },
            { (int)GlobalAxisVals.AXIS_RZ, (int)HID_USAGES.HID_USAGE_RZ },
            { (int)GlobalAxisVals.AXIS_M1, (int)HID_USAGES.HID_USAGE_SL0 },
            { (int)GlobalAxisVals.AXIS_M2, (int)HID_USAGES.HID_USAGE_SL1 }
        };

        /// <summary>
        /// The ID of the controller.
        /// </summary>
        public uint ControllerID { get; private set; } = 0;

        public int ControllerIndex => (int)ControllerID;

        /// <summary>
        /// Tells whether the controller device was successfully acquired through vJoy.
        /// If this is false, don't use this controller instance to make inputs.
        /// </summary>
        public bool IsAcquired { get; private set; } = false;

        /// <summary>
        /// The JoystickState of the controller, used in the Efficient implementation.
        /// </summary>
        private JoystickState JSState = default;

        private Dictionary<int, (long AxisMin, long AxisMax)> MinMaxAxes = new Dictionary<int, (long, long)>(8);

        private vJoy VJoyInstance = null;

        public VJoyController(in uint controllerID, vJoy vjoyInstance)
        {
            ControllerID = controllerID;
            VJoyInstance = vjoyInstance;
        }

        public void Dispose()
        {
            if (IsAcquired == false)
                return;

            Reset();
            Close();
        }

        public void Acquire()
        {
            IsAcquired = VJoyInstance.AcquireVJD(ControllerID);
        }

        public void Close()
        {
            VJoyInstance.RelinquishVJD(ControllerID);
            IsAcquired = false;
        }

        public void Init()
        {
            Reset();

            //Initialize axes
            //Use the global axes values, which will be converted to vJoy ones when needing to carry out the inputs
            GlobalAxisVals[] axes = EnumUtility.GetValues<GlobalAxisVals>.EnumValues;

            for (int i = 0; i < axes.Length; i++)
            {
                int globalAxisVal = (int)axes[i];

                if (AxisCodeMap.TryGetValue(globalAxisVal, out int axisVal) == false)
                {
                    continue;
                }

                HID_USAGES vJoyAxis = (HID_USAGES)axisVal;

                if (VJoyInstance.GetVJDAxisExist(ControllerID, vJoyAxis))
                {
                    long min = 0L;
                    long max = 0L;
                    VJoyInstance.GetVJDAxisMin(ControllerID, vJoyAxis, ref min);
                    VJoyInstance.GetVJDAxisMax(ControllerID, vJoyAxis, ref max);

                    MinMaxAxes.Add(globalAxisVal, (min, max));
                }
            }
        }

        public void Reset()
        {
            if (IsAcquired == false)
                return;

            JSState.Buttons = JSState.ButtonsEx1 = JSState.ButtonsEx2 = JSState.ButtonsEx3 = 0;

            foreach (KeyValuePair<int, (long, long)> val in MinMaxAxes)
            {
                if (val.Key == (int)GlobalAxisVals.AXIS_Z || val.Key == (int)GlobalAxisVals.AXIS_RZ)
                {
                    ReleaseAbsoluteAxis(val.Key);
                }
                else
                {
                    ReleaseAxis(val.Key);
                }
            }

            UpdateController();
        }

        public void PressInput(in Parser.Input input)
        {
            if (InputGlobals.CurrentConsole.IsAbsoluteAxis(input) == true)
            {
                PressAbsoluteAxis(InputGlobals.CurrentConsole.InputAxes[input.name], input.percent);

                //Kimimaru: In the case of L and R buttons on GCN, when the axes are pressed, the buttons should be released
                ReleaseButton(input.name);
            }
            else if (InputGlobals.CurrentConsole.GetAxis(input, out int axis) == true)
            {
                PressAxis(axis, InputGlobals.CurrentConsole.IsMinAxis(input), input.percent);
            }
            else if (InputGlobals.CurrentConsole.IsButton(input) == true)
            {
                PressButton(input.name);

                //Kimimaru: In the case of L and R buttons on GCN, when the buttons are pressed, the axes should be released
                if (InputGlobals.CurrentConsole.InputAxes.TryGetValue(input.name, out int value) == true)
                {
                    ReleaseAbsoluteAxis(value);
                }
            }
        }

        public void ReleaseInput(in Parser.Input input)
        {
            if (InputGlobals.CurrentConsole.IsAbsoluteAxis(input) == true)
            {
                ReleaseAbsoluteAxis(InputGlobals.CurrentConsole.InputAxes[input.name]);

                //Kimimaru: In the case of L and R buttons on GCN, when the axes are released, the buttons should be too
                ReleaseButton(input.name);
            }
            else if (InputGlobals.CurrentConsole.GetAxis(input, out int axis) == true)
            {
                ReleaseAxis(axis);
            }
            else if (InputGlobals.CurrentConsole.IsButton(input) == true)
            {
                ReleaseButton(input.name);

                //Kimimaru: In the case of L and R buttons on GCN, when the buttons are released, the axes should be too
                if (InputGlobals.CurrentConsole.InputAxes.TryGetValue(input.name, out int value) == true)
                {
                    ReleaseAbsoluteAxis(value);
                }
            }
        }

        public void PressAxis(in int axis, in bool min, in int percent)
        {
            if (MinMaxAxes.TryGetValue(axis, out (long, long) axisVals) == false)
            {
                return;
            }

            //Neutral is halfway between the min and max axes 
            long half = (axisVals.Item2 - axisVals.Item1) / 2L;
            int mid = (int)(axisVals.Item1 + half);
            int val = 0;

            if (min)
            {
                val = (int)(mid - ((percent / 100f) * half));
            }
            else
            {
                val = (int)(mid + ((percent / 100f) * half));
            }

            AxisCodeMap.TryGetValue(axis, out int vJoyAxis);

            SetAxisEfficient(vJoyAxis, val);
        }

        public void ReleaseAxis(in int axis)
        {
            if (MinMaxAxes.TryGetValue(axis, out (long, long) axisVals) == false)
            {
                return;
            }

            //Neutral is halfway between the min and max axes
            long half = (axisVals.Item2 - axisVals.Item1) / 2L;
            int val = (int)(axisVals.Item1 + half);

            AxisCodeMap.TryGetValue(axis, out int vJoyAxis);

            SetAxisEfficient(vJoyAxis, val);
        }

        public void PressAbsoluteAxis(in int axis, in int percent)
        {
            if (MinMaxAxes.TryGetValue(axis, out (long, long) axisVals) == false)
            {
                return;
            }

            int val = (int)(axisVals.Item2 * (percent / 100f));

            AxisCodeMap.TryGetValue(axis, out int vJoyAxis);

            SetAxisEfficient(vJoyAxis, val);
        }

        public void ReleaseAbsoluteAxis(in int axis)
        {
            if (MinMaxAxes.ContainsKey(axis) == false)
            {
                return;
            }

            AxisCodeMap.TryGetValue(axis, out int vJoyAxis);

            SetAxisEfficient(vJoyAxis, 0);
        }

        public void PressButton(in string buttonName)
        {
            uint buttonVal = InputGlobals.CurrentConsole.ButtonInputMap[buttonName];

            //Kimimaru: Handle button counts greater than 32
            //Each buttons value contains 32 bits, so choose the appropriate one based on the value of the button pressed
            //Note that not all emulators (such as Dolphin) support more than 32 buttons
            int buttonDiv = ((int)buttonVal - 1);
            int divVal = buttonDiv / 32;
            int realVal = buttonDiv - (32 * divVal);
            uint addition = (uint)(1 << realVal);

            switch (divVal)
            {
                case 0: JSState.Buttons |= addition; break;
                case 1: JSState.ButtonsEx1 |= addition; break;
                case 2: JSState.ButtonsEx2 |= addition; break;
                case 3: JSState.ButtonsEx3 |= addition; break;
            }
        }

        public void ReleaseButton(in string buttonName)
        {
            uint buttonVal = InputGlobals.CurrentConsole.ButtonInputMap[buttonName];

            //Kimimaru: Handle button counts greater than 32
            //Each buttons value contains 32 bits, so choose the appropriate one based on the value of the button pressed
            //Note that not all emulators (such as Dolphin) support more than 32 buttons
            int buttonDiv = ((int)buttonVal - 1);
            int divVal = buttonDiv / 32;
            int realVal = buttonDiv - (32 * divVal);
            uint inverse = ~(uint)(1 << realVal);

            switch (divVal)
            {
                case 0: JSState.Buttons &= inverse; break;
                case 1: JSState.ButtonsEx1 &= inverse; break;
                case 2: JSState.ButtonsEx2 &= inverse; break;
                case 3: JSState.ButtonsEx3 &= inverse; break;
            }
        }

        public void UpdateController()
        {
            UpdateJoystickEfficient();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetAxisEfficient(in int axis, in int value)
        {
            switch (axis)
            {
                case (int)HID_USAGES.HID_USAGE_X: JSState.AxisX = value; break;
                case (int)HID_USAGES.HID_USAGE_Y: JSState.AxisY = value; break;
                case (int)HID_USAGES.HID_USAGE_Z: JSState.AxisZ = value; break;
                case (int)HID_USAGES.HID_USAGE_RX: JSState.AxisXRot = value; break;
                case (int)HID_USAGES.HID_USAGE_RY: JSState.AxisYRot = value; break;
                case (int)HID_USAGES.HID_USAGE_RZ: JSState.AxisZRot = value; break;
            }
        }

        /// <summary>
        /// Updates the joystick.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateJoystickEfficient()
        {
            VJoyInstance.UpdateVJD(ControllerID, ref JSState);
        }
    }
}