using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Emulate6502.Memory;

namespace Emulate6502.CpuObjects
{
    public enum AddressingMode
    {
        Immediate = 0,
        ZeroPage = 1,
        ZeroPageX = 2,
        Absolute = 3,
        AbsoluteX = 4,
        AbsoluteY = 5,
        IndirectX = 6,
        IndirectY = 7,
        IndirectAbsolute = 8,
        ZeroPageY = 9,
        Relative = 10,
        Accumulator = 11
    }

    public enum Registers
    {
        Accumulator = 0,
        X = 1,
        Y = 2,
        StackPointer = 3,
        ProcessorStatus = 4
    }

    public class Cpu
    {
        public static ushort IRQ_VECTOR_ADDR = 0xFFFE;
        public static ushort NMI_VECTOR_ADDR = 0xFFFA;
        public static ushort RESET_VECTOR_ADDR = 0xFFFC;

        private byte _accumulator;
        private byte _x, _y;

        private ushort _pc;
        //private byte _sp;
        private byte _ps;

        private byte _cycles;
        private long _emulatedCycles;
        private OpCodes _opCode;

        private bool _break;

        private const byte CYCLE_PERIOD = 64;
        private const long CYCLES_PER_SECOND = 179;
        private const long MULTIPLIER = 10000;
        private long _runTicks = 0;
        private long _sleepTicks = 0;

        //capture data on executed opcodes
        private bool _shouldCaptureOpCode = false;
        private Dictionary<OpCodes, int> _executedCodes;


        //indicates the current offset of pc from the current opcode address
        private byte _pcOffset;

        //does the current instruction access a different 
        //page to what it's executing on ?
        private bool _crossesPageBoundary = false;

        //cycle map to map opcodes to cycles
        private CycleMapper _opCodeCycleMap;

        //parent emulator
        Emulator.NesEmulator _parent;

        public Cpu(Emulator.NesEmulator parent)
        {
            _parent = parent;
            _pc = 0;
            _accumulator = 0;
            _x = 0;
            _y = 0;
            _cycles = CYCLE_PERIOD;
            _opCode = OpCodes.ADC_A;
            _break = false;
            _runTicks = TimeSpan.FromMilliseconds(1.0 / CYCLES_PER_SECOND).Ticks;
            _sleepTicks = _runTicks / (MULTIPLIER * 50);
            _opCodeCycleMap = new CycleMapper();
            _executedCodes = new Dictionary<OpCodes, int>();
        }

        #region Accessible Architecture Features
        public bool CaptureOpCodes
        {
            set
            {
                _shouldCaptureOpCode = value;
            }
        }

        public Dictionary<OpCodes, int> ExecutedOpCodes
        {
            get
            {
                return _executedCodes;
            }
        }

        //we need to expose the PC just to enable full flow control
        //for all logic that needs it like debuggers. This is easier
        //than some complex system
        public ushort ProgramCounter
        {
            get
            {
                return _pc;
            }
            set
            {
                _pc = value;
            }
        }

        public long EmulatedCycles
        {
            get
            {
                return _emulatedCycles;
            }
        }

        //Need to expose the regsiter values as part of debugging
        //These are read only though
        public byte X
        {
            get
            {
                return _x;
            }
            set
            {
                _x = value;
            }
        }

        public byte Y
        {
            get
            {
                return _y;
            }
            set
            {
                _y = value;
            }
        }

        public byte Accumulator
        {
            get
            {
                return _accumulator;
            }
            set
            {
                _accumulator = value;
            }
        }

        public byte StackPointer
        {
            get
            {
                return _parent.Stack.StackPointer;
            }
            set
            {
                _parent.Stack.StackPointer = value;
            }
        }

        public byte ProcessorStatus
        {
            get
            {
                return _ps;
            }
            set
            {
                _ps = value;
            }
        }

        #endregion

        #region Flag Operations Helper
        public bool IsCarry
        {
            get
            {
                return (_ps & 0x01) == 0x01;
            }
            set
            {
                if (value)
                {
                    _ps |= 0x01;
                }
                else
                {
                    _ps &= 0xfe;
                }
            }
        }

        public bool IsZero
        {
            get
            {
                return (_ps & 0x02) == 0x02;
            }
            set
            {
                if (value)
                {
                    _ps |= 0x02;
                }
                else
                {
                    _ps &= 0xfd;
                }
            }
        }

        public bool InterruptsEnabled
        {
            get
            {
                return (_ps & 0x04) != 0x04;
            }
            set
            {
                if (!value)
                {
                    _ps |= 0x04;
                }
                else
                {
                    _ps &= 0xfb;
                }
            }
        }

        public bool IsDecimalMode
        {
            get
            {
                return (_ps & 0x08) == 0x08;
            }
            set
            {
                if (value)
                {
                    _ps |= 0x08;
                }
                else
                {
                    _ps &= 0xf7;
                }
            }
        }

        public bool IsSoftwareInterrupt
        {
            get
            {
                return (_ps & 0x10) == 0x10;
            }
            set
            {
                if (value)
                {
                    _ps |= 0x10;
                }
                else
                {
                    _ps &= 0xef;
                }
            }
        }

        public bool IsOverflow
        {
            get
            {
                return (_ps & 0x40) == 0x20;
            }
            set
            {
                if (value)
                {
                    _ps |= 0x40;
                }
                else
                {
                    _ps &= 0xbf;
                }
            }
        }

        public bool IsPositive
        {
            get
            {
                return (_ps & 0x80) != 0x80;
            }
            set
            {
                if (!value)
                {
                    _ps |= 0x80;
                }
                else
                {
                    _ps &= 0x7f;
                }
            }
        }

        private void SetSign(uint result)
        {
            _ps = ((result & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
        }

        private void SetZero(uint result)
        {
            _ps = ((result & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
        }

        private void SetCarry(uint result)
        {
            _ps = (result > 0xff) ? _ps |= 0x01 : _ps &= 0xfe;
        }

        private void SetOverflow(uint memVal, uint result)
        {
            //if acc and src are both positive then result should be 
            //positive, else if they are both negative result should be negative
            //so if sign bit of result is different to operands we have an overflow
            _ps = !(((_accumulator ^ memVal) & 0x80) == 0x80) && (((_accumulator ^ result) & 0x80) == 0x80) ? _ps |= 0x40 : _ps &= 0xbf;
        }
        #endregion

        #region Memory Helpers
        //originalValue can be 0 if addressing mode doesn't require it
        private ushort TranslateAddress(ushort originalValue, AddressingMode mode)
        {
            switch (mode)
            {
                case AddressingMode.Absolute:
                    return originalValue;
                case AddressingMode.AbsoluteY:
                    return (ushort)(originalValue + (_y));
                case AddressingMode.AbsoluteX:
                    return (ushort)(originalValue + (_x));
                case AddressingMode.ZeroPageX:
                    return (ushort)(originalValue + (_x));
                case AddressingMode.ZeroPageY:
                    return (ushort)(originalValue + (_y));
                case AddressingMode.Immediate:
                    return originalValue;
                case AddressingMode.Relative:
                    {
                        //if this is a negative value take the 2's complement
                        if ((originalValue & 0x80) == 0x80)
                        {
                            byte newValue = (byte)originalValue;
                            
                            //negate the value and add 1
                            newValue ^= 0xFF;
                            newValue += 1;

                            return (ushort)(_pc - (newValue));
                        }
                        else
                        {
                            return (ushort)(_pc + originalValue);
                        }
                    }
                case AddressingMode.IndirectAbsolute:
                    {
                        ushort buffer = (ushort)_parent.MainMemory.Read(originalValue);
                        buffer |= (ushort)((_parent.MainMemory.Read((ushort)(originalValue + 1))) << 8);

                        return buffer;
                    }
                case AddressingMode.IndirectX:
                    {
                        uint finalAddress = originalValue + (uint)_x;
                        ushort buffer = (ushort)_parent.MainMemory.Read(finalAddress);
                        buffer |= (ushort)((_parent.MainMemory.Read((ushort)(finalAddress + 1))) << 8);

                        return buffer;
                    }
                case AddressingMode.IndirectY:
                    {
                        ushort buffer = (ushort)_parent.MainMemory.Read(originalValue);
                        buffer |= (ushort)((_parent.MainMemory.Read((ushort)(originalValue + 1))) << 8);

                        return (ushort)(buffer + _y);
                    }
                default:
                    return originalValue;
            }
        }

        private void TranslateValueToMemory(AddressingMode nextInstrAddr, byte finalValue)
        {
            ushort tempAdd = 0;
            bool isMemValSet = false;

            //if we're offset from the instruction, lets get back to par
            if (_pcOffset > 0)
            {
                _pc -= _pcOffset;
            }

            switch (nextInstrAddr)
            {
                case AddressingMode.Absolute:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.Absolute);

                        break;
                    }
                case AddressingMode.AbsoluteX:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.AbsoluteX);

                        break;
                    }
                case AddressingMode.AbsoluteY:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.AbsoluteY);

                        break;
                    }
                case AddressingMode.Accumulator:
                    {
                        _accumulator = finalValue;
                        isMemValSet = true;

                        break;
                    }
                case AddressingMode.IndirectAbsolute:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.IndirectAbsolute);

                        break;
                    }
                case AddressingMode.IndirectX:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.IndirectX);

                        break;
                    }
                case AddressingMode.IndirectY:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.IndirectY);

                        break;
                    }
                case AddressingMode.Relative:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.Relative);
                        isMemValSet = true;

                        break;
                    }
                case AddressingMode.ZeroPage:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.ZeroPage);

                        break;
                    }
                case AddressingMode.ZeroPageX:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.ZeroPageX);

                        break;
                    }
                case AddressingMode.ZeroPageY:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.ZeroPageY);

                        break;
                    }
            }

            if (!isMemValSet)
            {
                _parent.MainMemory.Write(tempAdd, finalValue);
            }
        }

        private byte TranslateToMemoryValue(AddressingMode nextInstrAddr)
        {
            ushort tempAdd = 0;
            bool isMemValSet = false;
            byte finalVal = 0;

            //if we're offset from the instruction, lets get back to par
            if (_pcOffset > 0)
            {
                _pc -= _pcOffset;
            }

            switch (nextInstrAddr)
            {
                case AddressingMode.Absolute:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.Absolute);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.AbsoluteX:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.AbsoluteX);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.AbsoluteY:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.AbsoluteY);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.Immediate:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        finalVal = (byte)tempAdd;
                        isMemValSet = true;

                        break;
                    }
                case AddressingMode.Accumulator:
                    {
                        finalVal = _accumulator;
                        isMemValSet = true;

                        break;
                    }
                case AddressingMode.IndirectAbsolute:
                    {
                        tempAdd = Read16bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.IndirectAbsolute);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.IndirectX:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.IndirectX);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.IndirectY:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.IndirectY);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.Relative:
                    {
                        finalVal |= Read8bitAddressAtPC();
                        //tempAdd = TranslateAddress(tempAdd, AddressingMode.Relative);
                        isMemValSet = true;

                        break;
                    }
                case AddressingMode.ZeroPage:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.ZeroPage);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.ZeroPageX:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.ZeroPageX);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
                case AddressingMode.ZeroPageY:
                    {
                        tempAdd |= Read8bitAddressAtPC();
                        tempAdd = TranslateAddress(tempAdd, AddressingMode.ZeroPageY);

                        _crossesPageBoundary = ((_pc & 0xFF00) != (tempAdd & 0xFF00)); 

                        break;
                    }
            }

            if (!isMemValSet)
            {
                finalVal = _parent.MainMemory.Read(tempAdd);
            }

            return finalVal;
        }

        private ushort Read16bitAddressAtPC()
        {
            ushort temp = 0;

            temp = _parent.MainMemory.Read(_pc++);
            temp |= (ushort)((ushort)_parent.MainMemory.Read(_pc++) << 8);
            _pcOffset += 2;

            return temp;
        }

        private ushort Read16bitAddressAt(ushort address)
        {
            ushort temp = 0;

            temp = _parent.MainMemory.Read(address++);
            temp |= (ushort)((ushort)_parent.MainMemory.Read(address) << 8);

            return temp;
        }

        private byte Read8bitAddressAtPC()
        {
            byte value;

            value = _parent.MainMemory.Read(_pc++);
            _pcOffset++;

            return value;
        }

        #endregion

        #region IRQ Handling
        public void DoIRQ()
        {
            if (InterruptsEnabled)
            {
                //IRQ takes same cycles as BRK interrupt
                _cycles = _opCodeCycleMap[OpCodes.BRK];
                RaiseInterrupt(IRQ_VECTOR_ADDR, false);
                _emulatedCycles += _cycles;
            }
        }

        public void DoNMI()
        {
            if (_parent.PPU.IsNMIEnabled)
            {
                //NMI takes same cycles as BRK interrupt
                _cycles = _opCodeCycleMap[OpCodes.BRK];
                RaiseInterrupt(NMI_VECTOR_ADDR, false);
                _emulatedCycles += _cycles;
            }
        }
        #endregion

        #region OpCode Helpers
        private void AddOpCodeEntry(OpCodes code)
        {
            if (_executedCodes.ContainsKey(code))
            {
                _executedCodes[code]++;
            }
            else
            {
                _executedCodes.Add(code, 1);
            }
        }

        private void AddWithCarry(uint memVal)
        {
            int tempResult = 0;

            //perform the operation
            tempResult = (int)memVal + (int)_accumulator;

            if (IsCarry)
            {
                tempResult++;
            }

            _ps = (tempResult > 0xff) ? _ps |= 0x01 : _ps &= 0xfe;
            _ps = !(((_accumulator ^ memVal) & 0x80) == 0x80) && (((_accumulator ^ tempResult) & 0x80) == 0x80) ? _ps |= 0x40 : _ps &= 0xbf;

            _accumulator = (byte)tempResult;

            _ps = ((_accumulator & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((_accumulator & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
        }

        private void And(uint memVal)
        {
            memVal &= _accumulator;

            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            _accumulator = (byte)memVal;
        }

        private void Xor(uint memVal)
        {
            memVal ^= _accumulator;
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
            _accumulator = (byte)memVal;
        }

        private void Or(uint memVal)
        {
            memVal |= _accumulator;
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
            _accumulator = (byte)memVal;
        }

        private byte ArithmeticShiftLeft(uint memVal)
        {
            _ps = ((memVal & 0x80) == 0x80) ? _ps |= 0x01 : _ps &= 0xfe;
            memVal <<= 1;
            memVal &= 0xff;

            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            return (byte)memVal;
        }

        private byte ArithmeticShiftRight(uint memVal)
        {
            _ps = ((memVal & 0x01) == 0x01) ? _ps |= 0x01 : _ps &= 0xfe;
            memVal >>= 1;

            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            return (byte)memVal;
        }


        private void BranchOnCarry(uint addr, bool isSet)
        {
            if (((_ps & 0x01) == 0x01) == isSet)
            {
                addr = TranslateAddress((ushort)addr, AddressingMode.Relative);
                _cycles += (byte)(((_pc & 0xFF00) != (addr & 0xFF00)) ? 2 : 1); 


                //performing jump now (we need to do the translate specifically for the branch
                //doesn't make sense otherwise, we let the memory translators just grab
                //the immediate byte for the address
                _pc = (ushort)addr;
            }
        }


        private void BranchOnZero(uint addr, bool isSet)
        {
            if (((_ps & 0x02) == 0x02) == isSet)
            {
                addr = TranslateAddress((ushort)addr, AddressingMode.Relative);
                _cycles += (byte)(((_pc & 0xFF00) != (addr & 0xFF00)) ? 2 : 1); 

                _pc = (ushort)addr;
            }
        }

        private void BranchOnSign(uint addr, bool isSet)
        {
            if (((_ps & 0x80) == 0x80) == isSet)
            {
                addr = TranslateAddress((ushort)addr, AddressingMode.Relative);
                _cycles += (byte)(((_pc & 0xFF00) != (addr & 0xFF00)) ? 2 : 1);  

                _pc = (ushort)addr;
            }
        }

        private void BranchOnOverflow(uint addr, bool isSet)
        {
            if (((_ps & 0x40) == 0x40) == isSet)
            {
                addr = TranslateAddress((ushort)addr, AddressingMode.Relative);
                _cycles += (byte)(((_pc & 0xFF00) != (addr & 0xFF00)) ? 2 : 1); 

                _pc = (ushort)addr;
            }
        }

        private void TestBits(uint memVal)
        {
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            IsOverflow = (memVal & 0x40) == 0x40;
            _ps = (((memVal & _accumulator) & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
        }

        private void Break()
        {
            RaiseInterrupt(IRQ_VECTOR_ADDR, true);
        }

        private void RaiseInterrupt(ushort vector_addr, bool isSoftware)
        {
            _pc++;
            _parent.Stack.Push((byte)((_pc >> 8) & 0xff));
            _parent.Stack.Push((byte)(_pc & 0xff));
            IsSoftwareInterrupt = isSoftware;
            _parent.Stack.Push(_ps);
            InterruptsEnabled = false;
            _pc = TranslateAddress(vector_addr, AddressingMode.IndirectAbsolute);
        }

        private void Compare(uint memVal, byte reg)
        {
            int val = 0;
            int temp = (int)memVal;
            switch (reg)
            {
                case 0:
                    val = _accumulator;
                    break;
                case 1:
                    val = _x;
                    break;
                case 2:
                    val = _y;
                    break;
            }

            if (val >= memVal)
            {
                _ps |= 0x01;
            }
            else
            {
                _ps &= 0xfe;
            }

            temp = (int)(val - memVal);

            _ps = ((temp & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((temp & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            temp &= 0xff;
        }

        private byte Decrement(uint memVal)
        {
            byte temp = (byte)memVal;
            temp--;
            //memVal = (memVal - 1) & 0xff;
            memVal = temp;
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            return (byte)memVal;
        }

        //wrapping around of bytes is important
        private byte Increment(uint memVal)
        {
            byte val = (byte)memVal;
            val++;

            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            return val;
        }

        private void Decrement(byte reg)
        {
            byte temp = (reg == 0) ? _x : _y;
            temp--;

            _ps = ((temp & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((temp & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            if (reg == 0)
            {
                _x = (byte)temp;
            }
            else
            {
                _y = (byte)temp;
            }
        }

        private void Increment(byte reg)
        {   
            byte temp = (reg == 0) ? _x : _y;
            temp++;

            _ps = ((temp & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((temp & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            if (reg == 0)
            {
                _x = (byte)temp;
            }
            else
            {
                _y = (byte)temp;
            }
        }

        private void Jump(bool fromMemory, bool shouldSave)
        {
            ushort operand = Read16bitAddressAtPC();
            ushort addr = operand;

            if (fromMemory)
            {
                addr = Read16bitAddressAt(operand);
            }

            if (shouldSave)
            {
                //how far back should we be going?
                _pc--;
                _parent.Stack.Push((byte)((_pc >> 8) & 0xff));
                _parent.Stack.Push((byte)(_pc & 0xff));
            }

            _pc = (ushort)addr;
        }

        private void Load(uint memVal, byte reg)
        {
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            switch (reg)
            {
                case 0:
                    _accumulator = (byte)memVal;
                    break;
                case 1:
                    _x = (byte)memVal;
                    break;
                case 2:
                    _y = (byte)memVal;
                    break;
            }
        }

        private void PullFromStack(byte reg)
        {
            switch (reg)
            {
                case 0:
                    _accumulator = _parent.Stack.Pop();
                    _ps = ((_accumulator & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
                    _ps = ((_accumulator & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
                    break;
                case 1:
                    _ps = _parent.Stack.Pop();
                    break;
            }
        }

        private void PushToStack(byte reg)
        {
            switch (reg)
            {
                case 0:
                    _parent.Stack.Push(_accumulator);
                    break;
                case 1:
                    _parent.Stack.Push(_ps);
                    break;
            }
        }

        private void SubtractWithCarry(uint memVal)
        {
            uint tempResult = 0;

            //perform the operation
            tempResult = (uint)_accumulator - (uint)memVal;

            if (!IsCarry)
            {
                tempResult--;
            }

            _ps = ((tempResult & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = !(((_accumulator ^ memVal) & 0x80) == 0x80) && (((_accumulator ^ tempResult) & 0x80) == 0x80) ? _ps |= 0x40 : _ps &= 0xbf;
            _ps = ((tempResult & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            if (tempResult > 0x00)
            {
                IsCarry = true;
            }

            _accumulator = (byte)(tempResult & 0xff);
        }

        private byte RotateLeft(uint memVal)
        {
            memVal <<= 1;
            memVal |= (_ps & 0x01) == 0x01  ? (uint)0x01 : (uint)0x00;
            _ps = (memVal > 0xff) ? _ps |= 0x01 : _ps &= 0xfe;
            memVal &= 0xff;
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            return (byte)memVal;
        }

        private byte RotateRight(uint memVal)
        {
            memVal |= ((_ps & 0x01) == 0x01) ? (uint)0x100 : 0x000;
            _ps = ((memVal & 0x01) == 0x01) ? _ps |= 0x01 : _ps &= 0xfe;
            memVal >>= 1;
            _ps = ((memVal & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
            _ps = ((memVal & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;

            return (byte)memVal;
        }

        private void ReturnFromInterrupt()
        {
            _ps = _parent.Stack.Pop();
            _pc = _parent.Stack.Pop();
            _pc |= (ushort)((ushort)_parent.Stack.Pop() << 8);
        }

        private void ReturnFromSubroutine()
        {
            _pc = _parent.Stack.Pop();
            _pc |= (ushort)((ushort)_parent.Stack.Pop() << 8);

            _pc++;
        }

        private byte Store(byte reg)
        {
            switch (reg)
            {
                case 0:
                    return _accumulator;
                case 1:
                    return _x;
                case 2:
                    return _y;
            }

            return 0;
        }

        private void Transfer(Registers from, Registers to)
        {
            byte src = 0;
            int txsTest = 0;

            switch (from)
            {
                case Registers.Accumulator:
                    src = _accumulator;
                    break;
                case Registers.StackPointer:
                    src = _parent.Stack.StackPointer;
                    break;
                case Registers.X:
                    src = _x;
                    txsTest++;
                    break;
                case Registers.Y:
                    src = _y;
                    break;
            }

            switch (to)
            {
                case Registers.Accumulator:
                    _accumulator = src;
                    break;
                case Registers.StackPointer:
                    _parent.Stack.StackPointer = src;
                    txsTest++;
                    break;
                case Registers.X:
                    _x = (byte)src;
                    break;
                case Registers.Y:
                    _y = (byte)src;
                    break;
            }

            //if we are not doing TXS operation
            if (txsTest != 2)
            {
                _ps = ((src & 0x80) != 0x80) ? _ps &= 0x7f : _ps |= 0x80;
                _ps = ((src & 0xff) == 0x00) ? _ps |= 0x02 : _ps &= 0xfd;
            }
        }

        #endregion

        #region Execution Controllers
        //return cpu to start state
        public void Reset()
        {
            _ps = 0;
            _x = 0;
            _y = 0;
            _accumulator = 0;
            _cycles = 0;
            _emulatedCycles = 0;
            _pcOffset = 0;
            _crossesPageBoundary = false;

            //reset the stack
            _parent.Stack.Reset();

            //set pc to reset vector
            _pc = Read16bitAddressAt(RESET_VECTOR_ADDR);
            _emulatedCycles += _opCodeCycleMap[OpCodes.BRK];
        }

        //execute cpu for certain set of cycles
        public uint Execute(uint cycleCount)
        {
            int temp = (int)cycleCount;

            while (temp > 0)
            {
                temp -= ExecuteNext();
            }

            return (uint)((int)cycleCount - temp);
        }

        public void AddCPUCycles(uint cycles)
        {
            _emulatedCycles += cycles;
        }


        //executes the next statement pointed to be the program counter
        public byte ExecuteNext()
        {
            //reset the pc offset for instruction
            //as well as instruction cycle count
            //and some other instruction based state
            _pcOffset = 0;
            _cycles = 0;
            _crossesPageBoundary = false;

            //check with global debugger before executing
            //next statement.
            if (Debugger.Current.IsAttached)
            {
                //if the debugger is attached we will need to check if we can
                //continue execution before going forward
                Debugger.Current.Check(_pc);
            }

            //read the current opcode
            _opCode = (OpCodes)_parent.MainMemory.Read(_pc++);

            //set up default cycle information
            _cycles = _opCodeCycleMap[_opCode];

            //capture opcodes
            if (_shouldCaptureOpCode)
            {
                AddOpCodeEntry(_opCode);
            }

            #region Main Opcode Switch Statement
            //perform the opcode
            switch (_opCode)
            {
                //ADC
                case OpCodes.ADC_A:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.Absolute));
                    break;
                case OpCodes.ADC_Z:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.ZeroPage));
                    break;
                case OpCodes.ADC_ZX:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX));
                    break;
                case OpCodes.ADC_I:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.Immediate));
                    break;
                case OpCodes.ADC_AX:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.ADC_AY:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.AbsoluteY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.ADC_IX:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.IndirectX));
                    break;
                case OpCodes.ADC_IY:
                    AddWithCarry((uint)TranslateToMemoryValue(AddressingMode.IndirectY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;

                //AND
                case OpCodes.AND_A:
                    And((uint)TranslateToMemoryValue(AddressingMode.Absolute));
                    break;
                case OpCodes.AND_AX:
                    And((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.AND_AY:
                    And((uint)TranslateToMemoryValue(AddressingMode.AbsoluteY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.AND_I:
                    And((uint)TranslateToMemoryValue(AddressingMode.Immediate));
                    break;
                case OpCodes.AND_IX:
                    And((uint)TranslateToMemoryValue(AddressingMode.IndirectX));
                    break;
                case OpCodes.AND_IY:
                    And((uint)TranslateToMemoryValue(AddressingMode.IndirectY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.AND_Z:
                    And((uint)TranslateToMemoryValue(AddressingMode.ZeroPage));
                    break;
                case OpCodes.AND_ZX:
                    And((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX));
                    break;

                //ASL
                case OpCodes.ASL_AB:
                    TranslateValueToMemory(AddressingMode.Absolute, ArithmeticShiftLeft((uint)TranslateToMemoryValue(AddressingMode.Absolute)));
                    break;
                case OpCodes.ASL_A:
                    TranslateValueToMemory(AddressingMode.Accumulator, ArithmeticShiftLeft((uint)TranslateToMemoryValue(AddressingMode.Accumulator)));
                    break;
                case OpCodes.ASL_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, ArithmeticShiftLeft((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX)));
                    break;
                case OpCodes.ASL_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, ArithmeticShiftLeft((uint)TranslateToMemoryValue(AddressingMode.ZeroPage)));
                    break;
                case OpCodes.ASL_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, ArithmeticShiftLeft((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX)));
                    break;

                //Branching
                case OpCodes.BCC:
                    BranchOnCarry((uint)TranslateToMemoryValue(AddressingMode.Relative), false);
                    break;
                case OpCodes.BCS:
                    BranchOnCarry((uint)TranslateToMemoryValue(AddressingMode.Relative), true);
                    break;
                case OpCodes.BEQ:
                    BranchOnZero((uint)TranslateToMemoryValue(AddressingMode.Relative), true);
                    break;
                case OpCodes.BMI:
                    BranchOnSign((uint)TranslateToMemoryValue(AddressingMode.Relative), true);
                    break;
                case OpCodes.BNE:
                    BranchOnZero((uint)TranslateToMemoryValue(AddressingMode.Relative), false);
                    break;
                case OpCodes.BPL:
                    BranchOnSign((uint)TranslateToMemoryValue(AddressingMode.Relative), false);
                    break;
                case OpCodes.BVC:
                    BranchOnOverflow((uint)TranslateToMemoryValue(AddressingMode.Relative), false);
                    break;
                case OpCodes.BVS:
                    BranchOnOverflow((uint)TranslateToMemoryValue(AddressingMode.Relative), true);
                    break;

                //BRK
                case OpCodes.BRK: //(Advanced instruction will look into)
                    //make this optional
                    //Debugger.Launch();
                    Break();
                    break;
                //BIT
                case OpCodes.BIT_A:
                    TestBits((uint)TranslateToMemoryValue(AddressingMode.Absolute));
                    break;
                case OpCodes.BIT_Z:
                    TestBits((uint)TranslateToMemoryValue(AddressingMode.ZeroPage));
                    break;

                //Status Clear Operations
                case OpCodes.CLC:
                    IsCarry = false;
                    break;
                case OpCodes.CLD:
                    IsDecimalMode = false;
                    break;
                case OpCodes.CLI:
                    InterruptsEnabled = true;
                    break;
                case OpCodes.CLV:
                    IsOverflow = false;
                    break;

                //Compare Operations
                case OpCodes.CMP_A:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.Absolute), 0);
                    break;
                case OpCodes.CMP_AX:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX), 0);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.CMP_AY:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.AbsoluteY), 0);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.CMP_I:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.Immediate), 0);
                    break;
                case OpCodes.CMP_IX:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.IndirectX), 0);
                    break;
                case OpCodes.CMP_IY:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.IndirectY), 0);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.CMP_Z:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.ZeroPage), 0);
                    break;
                case OpCodes.CMP_ZX:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX), 0);
                    break;
                case OpCodes.CPX_A:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.Absolute), 1);
                    break;
                case OpCodes.CPX_I:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.Immediate), 1);
                    break;
                case OpCodes.CPX_Z:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.ZeroPage), 1);
                    break;
                case OpCodes.CPY_A:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.Absolute), 2);
                    break;
                case OpCodes.CPY_I:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.Immediate), 2);
                    break;
                case OpCodes.CPY_Z:
                    Compare((uint)TranslateToMemoryValue(AddressingMode.ZeroPage), 2);
                    break;

                //Decrement Operations
                case OpCodes.DEC_A:
                    TranslateValueToMemory(AddressingMode.Absolute, Decrement((uint)TranslateToMemoryValue(AddressingMode.Absolute)));
                    break;
                case OpCodes.DEC_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, Decrement((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX)));
                    break;
                case OpCodes.DEC_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, Decrement((uint)TranslateToMemoryValue(AddressingMode.ZeroPage)));
                    break;
                case OpCodes.DEC_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, Decrement((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX)));
                    break;
                case OpCodes.DEX:
                    Decrement((byte)0);
                    break;
                case OpCodes.DEY:
                    Decrement((byte)1);
                    break;

                //XOR
                case OpCodes.EOR_A:
                    Xor(TranslateToMemoryValue(AddressingMode.Absolute));
                    break;
                case OpCodes.EOR_AX:
                    Xor(TranslateToMemoryValue(AddressingMode.AbsoluteX));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.EOR_AY:
                    Xor(TranslateToMemoryValue(AddressingMode.AbsoluteY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.EOR_I:
                    Xor(TranslateToMemoryValue(AddressingMode.Immediate));
                    break;
                case OpCodes.EOR_IX:
                    Xor(TranslateToMemoryValue(AddressingMode.IndirectX));
                    break;
                case OpCodes.EOR_IY:
                    Xor(TranslateToMemoryValue(AddressingMode.IndirectY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.EOR_Z:
                    Xor(TranslateToMemoryValue(AddressingMode.ZeroPage));
                    break;
                case OpCodes.EOR_ZX:
                    Xor(TranslateToMemoryValue(AddressingMode.ZeroPageX));
                    break;

                //Increment
                case OpCodes.INC_A:
                    TranslateValueToMemory(AddressingMode.Absolute, Increment((uint)TranslateToMemoryValue(AddressingMode.Absolute)));
                    break;
                case OpCodes.INC_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, Increment((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX)));
                    break;
                case OpCodes.INC_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, Increment((uint)TranslateToMemoryValue(AddressingMode.ZeroPage)));
                    break;
                case OpCodes.INC_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, Increment((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX)));
                    break;
                case OpCodes.INX:
                    Increment((byte)0);
                    break;
                case OpCodes.INY:
                    Increment((byte)1);
                    break;

                //Jump Operations
                case OpCodes.JMP_A:
                    Jump(false, false);
                    break;
                case OpCodes.JMP_I:
                    Jump(true, false);
                    break;
                case OpCodes.JSR:
                    Jump(false, true);
                    break;

                //Load Operations
                case OpCodes.LDA_A:
                    Load(TranslateToMemoryValue(AddressingMode.Absolute), 0);
                    break;
                case OpCodes.LDA_AX:
                    Load(TranslateToMemoryValue(AddressingMode.AbsoluteX), 0);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.LDA_AY:
                    Load(TranslateToMemoryValue(AddressingMode.AbsoluteY), 0);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.LDA_I:
                    Load(TranslateToMemoryValue(AddressingMode.Immediate), 0);
                    break;
                case OpCodes.LDA_IX:
                    Load(TranslateToMemoryValue(AddressingMode.IndirectX), 0);
                    break;
                case OpCodes.LDA_IY:
                    Load(TranslateToMemoryValue(AddressingMode.IndirectY), 0);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.LDA_Z:
                    Load(TranslateToMemoryValue(AddressingMode.ZeroPage), 0);
                    break;
                case OpCodes.LDA_ZX:
                    Load(TranslateToMemoryValue(AddressingMode.ZeroPageX), 0);
                    break;
                case OpCodes.LDX_A:
                    Load(TranslateToMemoryValue(AddressingMode.Absolute), 1);
                    break;
                case OpCodes.LDX_AY:
                    Load(TranslateToMemoryValue(AddressingMode.AbsoluteY), 1);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.LDX_I:
                    Load(TranslateToMemoryValue(AddressingMode.Immediate), 1);
                    break;
                case OpCodes.LDX_Z:
                    Load(TranslateToMemoryValue(AddressingMode.ZeroPage), 1);
                    break;
                case OpCodes.LDX_ZY:
                    Load(TranslateToMemoryValue(AddressingMode.ZeroPageY), 1);
                    break;
                case OpCodes.LDY_A:
                    Load(TranslateToMemoryValue(AddressingMode.Absolute), 2);
                    break;
                case OpCodes.LDY_AX:
                    Load(TranslateToMemoryValue(AddressingMode.AbsoluteX), 2);
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.LDY_I:
                    Load(TranslateToMemoryValue(AddressingMode.Immediate), 2);
                    break;
                case OpCodes.LDY_Z:
                    Load(TranslateToMemoryValue(AddressingMode.ZeroPage), 2);
                    break;
                case OpCodes.LDY_ZX:
                    Load(TranslateToMemoryValue(AddressingMode.ZeroPageX), 2);
                    break;

                //LSR
                case OpCodes.LSR_A:
                    TranslateValueToMemory(AddressingMode.Absolute, ArithmeticShiftRight((uint)TranslateToMemoryValue(AddressingMode.Absolute)));
                    break;
                case OpCodes.LSR_AC:
                    TranslateValueToMemory(AddressingMode.Accumulator, ArithmeticShiftRight((uint)TranslateToMemoryValue(AddressingMode.Accumulator)));
                    break;
                case OpCodes.LSR_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, ArithmeticShiftRight((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX)));
                    break;
                case OpCodes.LSR_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, ArithmeticShiftRight((uint)TranslateToMemoryValue(AddressingMode.ZeroPage)));
                    break;
                case OpCodes.LSR_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, ArithmeticShiftRight((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX)));
                    break;

                //NOP
                case OpCodes.NOP:
                    break;

                //OR
                case OpCodes.ORA_A:
                    Xor(TranslateToMemoryValue(AddressingMode.Absolute));
                    break;
                case OpCodes.ORA_AX:
                    Xor(TranslateToMemoryValue(AddressingMode.AbsoluteX));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.ORA_AY:
                    Xor(TranslateToMemoryValue(AddressingMode.AbsoluteY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.ORA_I:
                    Xor(TranslateToMemoryValue(AddressingMode.Immediate));
                    break;
                case OpCodes.ORA_IX:
                    Xor(TranslateToMemoryValue(AddressingMode.IndirectX));
                    break;
                case OpCodes.ORA_IY:
                    Xor(TranslateToMemoryValue(AddressingMode.IndirectY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.ORA_Z:
                    Xor(TranslateToMemoryValue(AddressingMode.ZeroPage));
                    break;
                case OpCodes.ORA_ZX:
                    Xor(TranslateToMemoryValue(AddressingMode.ZeroPageX));
                    break;

                //Stack Operations
                case OpCodes.PHA:
                    PushToStack(0);
                    break;
                case OpCodes.PHP:
                    PushToStack(1);
                    break;
                case OpCodes.PLA:
                    PullFromStack(0);
                    break;
                case OpCodes.PLP:
                    PullFromStack(1);
                    break;

                //ROL
                case OpCodes.ROL_A:
                    TranslateValueToMemory(AddressingMode.Absolute, RotateLeft((uint)TranslateToMemoryValue(AddressingMode.Absolute)));
                    break;
                case OpCodes.ROL_AC:
                    TranslateValueToMemory(AddressingMode.Accumulator, RotateLeft((uint)TranslateToMemoryValue(AddressingMode.Accumulator)));
                    break;
                case OpCodes.ROL_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, RotateLeft((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX)));
                    break;
                case OpCodes.ROL_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, RotateLeft((uint)TranslateToMemoryValue(AddressingMode.ZeroPage)));
                    break;
                case OpCodes.ROL_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, RotateLeft((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX)));
                    break;

                //ROR
                case OpCodes.ROR_A:
                    TranslateValueToMemory(AddressingMode.Absolute, RotateRight((uint)TranslateToMemoryValue(AddressingMode.Absolute)));
                    break;
                case OpCodes.ROR_AC:
                    TranslateValueToMemory(AddressingMode.Accumulator, RotateRight((uint)TranslateToMemoryValue(AddressingMode.Accumulator)));
                    break;
                case OpCodes.ROR_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, RotateRight((uint)TranslateToMemoryValue(AddressingMode.AbsoluteX)));
                    break;
                case OpCodes.ROR_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, RotateRight((uint)TranslateToMemoryValue(AddressingMode.ZeroPage)));
                    break;
                case OpCodes.ROR_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, RotateRight((uint)TranslateToMemoryValue(AddressingMode.ZeroPageX)));
                    break;

                //Return from interrupt/subroutine
                case OpCodes.RTI:
                    ReturnFromInterrupt();
                    break;
                case OpCodes.RTS:
                    ReturnFromSubroutine();
                    break;

                //SBC
                case OpCodes.SBC_A:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.Absolute));
                    break;
                case OpCodes.SBC_AX:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.AbsoluteX));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.SBC_AY:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.AbsoluteY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.SBC_I:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.Immediate));
                    break;
                case OpCodes.SBC_IX:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.IndirectX));
                    break;
                case OpCodes.SBC_IY:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.IndirectY));
                    _cycles += (byte)(_crossesPageBoundary ? 1 : 0);
                    break;
                case OpCodes.SBC_Z:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.ZeroPage));
                    break;
                case OpCodes.SBC_ZX:
                    SubtractWithCarry(TranslateToMemoryValue(AddressingMode.ZeroPageX));
                    break;

                //Flag set operations
                case OpCodes.SEC:
                    IsCarry = true;
                    break;
                case OpCodes.SED:
                    IsDecimalMode = true;
                    break;
                case OpCodes.SEI:
                    InterruptsEnabled = false;
                    break;

                //Store operations
                //Accumulator
                case OpCodes.STA_A:
                    TranslateValueToMemory(AddressingMode.Absolute, Store(0));
                    break;
                case OpCodes.STA_AX:
                    TranslateValueToMemory(AddressingMode.AbsoluteX, Store(0));
                    break;
                case OpCodes.STA_AY:
                    TranslateValueToMemory(AddressingMode.AbsoluteY, Store(0));
                    break;
                case OpCodes.STA_IX:
                    TranslateValueToMemory(AddressingMode.IndirectX, Store(0));
                    break;
                case OpCodes.STA_IY:
                    TranslateValueToMemory(AddressingMode.IndirectY, Store(0));
                    break;
                case OpCodes.STA_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, Store(0));
                    break;
                case OpCodes.STA_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, Store(0));
                    break;

                //X
                case OpCodes.STX_A:
                    TranslateValueToMemory(AddressingMode.Absolute, Store(1));
                    break;
                case OpCodes.STX_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, Store(1));
                    break;
                case OpCodes.STX_ZY:
                    TranslateValueToMemory(AddressingMode.ZeroPageY, Store(1));
                    break;

                //Y
                case OpCodes.STY_A:
                    TranslateValueToMemory(AddressingMode.Absolute, Store(2));
                    break;
                case OpCodes.STY_Z:
                    TranslateValueToMemory(AddressingMode.ZeroPage, Store(2));
                    break;
                case OpCodes.STY_ZX:
                    TranslateValueToMemory(AddressingMode.ZeroPageX, Store(2));
                    break;

                //Transfer operations
                case OpCodes.TAX:
                    Transfer(Registers.Accumulator, Registers.X);
                    break;
                case OpCodes.TAY:
                    Transfer(Registers.Accumulator, Registers.Y);
                    break;
                case OpCodes.TSX:
                    Transfer(Registers.StackPointer, Registers.X);
                    break;
                case OpCodes.TXA:
                    Transfer(Registers.X, Registers.Accumulator);
                    break;
                case OpCodes.TXS:
                    Transfer(Registers.X, Registers.StackPointer);
                    break;
                case OpCodes.TYA:
                    Transfer(Registers.Y, Registers.Accumulator);
                    break;

                default:
                    //what should we do when encountering a bad opcode?
                    //throw or ignore.
                    //ignore currently
                    //throw new Exception("Bad opcode");
                    break;
            }
            #endregion

            //add instruction cycles to overall emulated cycles
            _emulatedCycles += _cycles;

            return _cycles;
        }

        #region Run Overrides
        public void Run()
        {
            Run(0x0000);
        }

        public void Run(ushort startAddr)
        {
            DateTime startTime = DateTime.Now, endTime = DateTime.Now;
            TimeSpan scratch = TimeSpan.FromMilliseconds(0.0);
            ushort tempAdd = 0;
            byte memVal = 0;
            uint tempResult;

            _pc = startAddr;

            for (; ; )
            {
                scratch = DateTime.Now.Subtract(startTime);
                scratch = TimeSpan.FromTicks(scratch.Ticks * MULTIPLIER);

                if (scratch.Ticks < _runTicks)
                {
                    Thread.Sleep(TimeSpan.FromTicks(_sleepTicks));
                    continue;
                }

                startTime = DateTime.Now;
                
                _cycles -= 1;//opcode counter;

                //exeute one more statement
                ExecuteNext();

                if (_cycles <= 0)
                {
                    if (_break)
                    {
                        break;
                    }
                }
            }
        }
        #endregion

        #endregion
    }
}
