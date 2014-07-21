using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Emulate6502.Memory;
using Emulate6502.Performance;

namespace Emulate6502.PPU
{
    public enum VRamIncOptions
    {
        VRamInc1 = 1,
        VRamInc32 = 32
    }

    public class PPU : AbstractMemoryMapper
    {
        public const uint NES_SCREEN_WIDTH = 256;
        public const uint NES_SCREEN_HEIGHT = 240;

        public const uint DMA_CPU_CYCLES = 0x200;
        public const uint EXECUTE_CYCLES_PER_SCANLINE = 0x90; //randomly picked as a good number

        public const uint VBLANK_SCANLINE_SIMUALTION = 30;

        //PPU current frame buffer
        private NESPixel[] _frameBuffer;
        private byte[] _frameBufferBytes;
        private byte[] _frameBufferInfo;

        //PPU memory map
        private Memory.Memory _ppuMemory;

        //PPU registers
        private byte _ppuControl0;
        private byte _ppuControl1;
        private byte _ppuStatus;

        //PPU will need parent emulator for access to rest of hardware
        Emulator.NesEmulator _parent;

        //this is shared by the ppu scrolling mechanism
        //These two will implement loopy's scrolling mechanism
        //value x is the horizontal scroll offset 0-7
        private ushort _vramAddressRegister;
        private ushort _vramAddressRegisterTemp;
        private byte _x;

        //vram read/write flipflop
        bool _isFirstWrite = false;

        //are non-maskable interrupts enabled ?
        bool _nmiEnabled = true;

        //sprite ram object
        SpriteRam _spriteRam;

        //Amount to increment vram address at port 2007h by on each write
        VRamIncOptions _vramInc = VRamIncOptions.VRamInc1;

        //loaded vram buffer (return when reading general vram before an actual vram read occurs)
        private byte _currentLoadedLatch;

        //Color palette
        Palette _nesPalette;

        //NameTables and AttributeTables
        private NameTables _nameTables;

        //represents the current scanline being drawn
        private uint _currentScanline;

        //pattern tables
        PatternTable _spriteTable, _bgTable;

        //palette color cache
        NESPixel[] _bgCache, _spCache;

        public PPU(Emulator.NesEmulator parent)
        {
            _parent = parent;

            InitialisePPU();
        }

        public Memory.Memory PPUMemory
        {
            get
            {
                return _ppuMemory;
            }
        }

        public Emulator.NesEmulator Emulator
        {
            get
            {
                return _parent;
            }
        }

        public byte[] LastFrame
        {
            get
            {
                //return SerializeFrame();
                return _frameBufferBytes;
            }
        }

        public void Reset()
        {
            //reset internal state
            _ppuControl0 = 0;
            _ppuControl1 = 0;
            _ppuStatus = 0;
            _vramAddressRegister = 0;
            _vramAddressRegisterTemp = 0;
            _x = 0;
            _currentScanline = 0;
            _currentLoadedLatch = 0;
            _vramInc = VRamIncOptions.VRamInc1;
            _isFirstWrite = false;
            _nmiEnabled = true;

            //recreate the frame buffer
            CreateFrameBuffer();

            //reset all referenced components
            _spriteTable.Reset();
            _bgTable.Reset();
            _nameTables.Reset();
            _nesPalette.Reset();
        }

        private byte[] SerializeFrame()
        {
            //1-byte for rgb and a
            byte[] pixels = new byte[_frameBuffer.Length * 4];
            uint i = 0;
            uint j = 0;

            foreach (var pixel in _frameBuffer)
            {
                pixels[i++] = pixel.R;
                pixels[i++] = pixel.G;
                pixels[i++] = pixel.B;

                //no alpha channel usage.
                pixels[i++] = 255;

                j++;
            }

            return pixels;
        }

        private void InitialisePPU()
        {
            //setup state variables
            _ppuMemory = new Memory.Memory();
            _spriteTable = new PatternTable(this, PatternTableSelection.Table0);
            _bgTable = new PatternTable(this, PatternTableSelection.Table1);
            _nameTables = new NameTables(this);
            _nesPalette = new Palette(this);
            _currentLoadedLatch = 0;
            _bgCache = new NESPixel[0x0F];
            _spCache = new NESPixel[0x0F];

            //create the frame buffer
            CreateFrameBuffer();

            if (_parent != null)
            {
                //attach our memory ranges to the cpu's main memory mapper architecture.
                _parent.MainMemory.AttachMemoryMapping(this, 0x2000, 0x3FFF);
                _parent.MainMemory.AttachMemoryMapping(this, 0x4014, 0x4014);
            }

            //initialise sprite ram
            _spriteRam = new SpriteRam(SpriteSizeOption.Sprite8x8);
        }

        private void CreateFrameBuffer()
        {
            _frameBuffer = new NESPixel[NES_SCREEN_HEIGHT * NES_SCREEN_WIDTH];
            _frameBufferInfo = new byte[NES_SCREEN_HEIGHT * NES_SCREEN_WIDTH];
            _frameBufferBytes = new byte[(NES_SCREEN_HEIGHT * NES_SCREEN_WIDTH) * 4];

            /*for (int i = 0; i < _frameBuffer.Length; i++)
            {
                _frameBuffer[i] = new NESPixel(0x00, 0x00, 0x00);
                _frameBufferInfo[i] = 0;
            }

            for (int i = 0; i < _frameBufferBytes.Length; i++)
            {
                if ((i % 4) == 3)
                {
                    _frameBufferBytes[i] = 0xFF;
                }
                else
                {
                    _frameBufferBytes[i] = 0x00;
                }
            }*/
        }

        #region MemoryMapper Members

        public override byte Read(uint address)
        {
            address = TranslateAddress(address);
            byte temp = 0;

            //ensure a valid address range
            if (address >= 0x2000
                && address <= 0x2007
                || address == 0x4014)
            {
                

                switch (address)
                {
                    case 0x2002:
                        {
                            temp = _ppuStatus;

                            //reset the vram flipflop
                            _isFirstWrite = false;

                            //clear the VBlank flag
                            _ppuStatus &= 0x7F;

                            break;
                        }
                    case 0x2004:
                        {
                            temp = _spriteRam.ReadData();
                            break;
                        }
                    case 0x2007:
                        {
                            temp = ReadVideoRam();
                            break;
                        }
                }
            }
            else
            {
                throw new IndexOutOfRangeException("Address out of PPU map range");
            }

            //handle memory check for sprite ram
            if (CpuObjects.Debugger.Current.IsAttached)
            {
                CpuObjects.Debugger.Current.CheckMemory(address, this, CpuObjects.MemoryOperation.Read, temp);
            }

            return temp;
        }

        #region Accessible Status Information
        public bool IsNMIEnabled
        {
            get
            {
                return (_ppuControl0 & 0x80) == 0x80;
            }
        }

        public bool IsSpritesVisible
        {
            get
            {
                return (_ppuControl1 & 0x10) == 0x10;
            }
        }

        public bool IsBackgroundVisible
        {
            get
            {
                return (_ppuControl1 & 0x08) == 0x08;
            }
        }

        public bool IsColorDisplay
        {
            get
            {
                //return (_ppuControl1 & 0x01) == 0x01;
                return true;
            }
        }


        public bool ShouldClipSpritesLeft
        {
            get
            {
                return (_ppuControl1 & 0x04) == 0x04;
            }
        }

        public bool IsVBlank
        {
            get
            {
                return (_ppuStatus & 0x80) == 0x80;
            }
        }

        public bool IsSprite0Hit
        {
            get
            {
                return (_ppuStatus & 0x40) == 0x40;
            }
            set
            {
                _ppuStatus |= 0x40;
            }
        }
        #endregion

        private byte ReadVideoRam()
        {
            ushort address = _vramAddressRegister;

            //increment the video ram address register by the amount 
            //indicated in the inc count
            _vramAddressRegister += (ushort)_vramInc;

            //data to return (and temp value for loaded latch)
            byte data = 0;
            byte temp = 0;

            //account for overall PPU mirroring
            address &= 0x3FFF;

            //use the PPU memory mapper to delegate memory
            //read tasks
            data = _ppuMemory.Read(address);

            //are we reading palette memory?
            //if so we can simply return the value
            if (address >= 0x3F00)
            {
                return data;
            }
            else
            {
                //if we're reading anything else, we need to account for the fact
                //that the PPU returns a buffered value thats sitting in the latch
                //and updates the latch buffer to the new value

                temp = _currentLoadedLatch;
                _currentLoadedLatch = data;

                return temp;
            }
        }

        public void WriteVideoRam(byte data)
        {
            ushort address = _vramAddressRegister;

            //increment the video ram address register by the amount 
            //indicated in the inc count
            _vramAddressRegister += (ushort)_vramInc;

            //account for overall PPU mirroring
            address &= 0x3FFF;

            //use the PPU memory mapper to delegate memory
            //read tasks
            _ppuMemory.Write(address, data);
        }

        private uint TranslateAddress(uint address)
        {
            if (address >= 0x2008 && address < 0x4000)
            {
                return address & 0x2007;
            }
            else
            {
                return address;
            }
        }

        public override void Write(uint address, byte value)
        {
            address = TranslateAddress(address);

            //handle memory check for sprite ram
            if (CpuObjects.Debugger.Current.IsAttached)
            {
                CpuObjects.Debugger.Current.CheckMemory(address, this, CpuObjects.MemoryOperation.Write, value);
            }

            //ensure a valid address range
            if (address >= 0x2000
                && address <= 0x2007
                || address == 0x4014)
            {
                byte temp = 0;

                switch (address)
                {
                    case 0x2000:
                        {
                            _ppuControl0 = value;
                            _spriteRam.SpriteSize = (SpriteSizeOption)((value & 0x20) >> 5);
                            _spriteTable.SetPatternTable((byte)((value & 0x08) >> 3));
                            _bgTable.SetPatternTable((byte)((value & 0x10) >> 4));
                            _vramInc = ((value & 0x04) == 0x04 ? VRamIncOptions.VRamInc32 : VRamIncOptions.VRamInc1);
                            _nmiEnabled = (value & 0x80) == 0x80;
                            //loopy's temp value
                            //we remove current bit 10,11 values and add in the name table scroll addresses.
                            _vramAddressRegisterTemp =
                                (ushort)((_vramAddressRegisterTemp & 0xF3FF) | ((((ushort)(value)) & 0x03) << 10));
                            break;
                        }
                    case 0x2001:
                        {
                            //research color emphasis
                            _ppuControl1 = value;
                            break;
                        }
                    case 0x2003:
                        {
                            _spriteRam.SpriteRamAddress = value;
                            break;
                        }
                    case 0x2004:
                        {
                            _spriteRam.WriteData(value);
                            break;
                        }
                    case 0x4014:
                        {
                            //simulate DMA sprite ram transfer
                            ushort cpuRamAddress = (ushort)(((ushort)value) << 8);
                            byte[] spriteData = new byte[SpriteRam.SPRITE_RAM_SIZE];

                            //read sprite data from cpu ram
                            _parent.MainMemory.ReadBlock(cpuRamAddress,
                                                            cpuRamAddress + SpriteRam.SPRITE_RAM_SIZE,
                                                            spriteData);

                            //write sprite data to sprite ram
                            _spriteRam.WriteDMA(spriteData);

                            //add dma cpu cycles to the count
                            _parent.CPU.AddCPUCycles(DMA_CPU_CYCLES);
                            break;
                        }
                    case 0x2005:
                        {
                            _isFirstWrite = !_isFirstWrite;

                            if (_isFirstWrite)
                            {
                                _vramAddressRegisterTemp = (ushort)((_vramAddressRegisterTemp & 0xFFE0) | ((((ushort)value) & 0xF8) >> 3));
                                _x = (byte)(value & 0x07);
                            }
                            else
                            {
                                _vramAddressRegisterTemp = (ushort)((_vramAddressRegisterTemp & 0xFC1F) | ((((ushort)value) & 0xF8) << 2));
                                _vramAddressRegisterTemp = (ushort)((_vramAddressRegisterTemp & 0x8FFF) | ((((ushort)value) & 0x07) << 12));
                            }

                            break;
                        }
                    case 0x2006:
                        {
                            _isFirstWrite = !_isFirstWrite;

                            if (_isFirstWrite)
                            {
                                _vramAddressRegisterTemp =
                                    (ushort)((_vramAddressRegisterTemp & 0x00FF) | ((((ushort)value) & 0x3F) << 8));
                            }
                            else
                            {
                                _vramAddressRegisterTemp = (ushort)((_vramAddressRegisterTemp & 0xFF00) | ((ushort)value));
                                _vramAddressRegister = _vramAddressRegisterTemp;
                            }

                            break;
                        }
                    case 0x2007:
                        {
                            WriteVideoRam(value);
                            break;
                        }

                }
            }
            else
            {
                throw new IndexOutOfRangeException("Address out of PPU map range");
            }
        }

        public override void ReadBlock(uint startAddress, uint endAddress, byte[] values)
        {
            throw new NotImplementedException();
        }

        public override void WriteBlock(uint startAddress, uint endAddress, byte[] values)
        {
            throw new NotImplementedException();
        }

        #endregion

        private void StartScanline(ref ushort vReg, ref ushort vRegTemp)
        {
            //start of horizontal scanline draw (loopy's scrolling info)
            vReg = (ushort)((vReg & 0xFBE0) | (vRegTemp & 0x041F));
        }

        #region Drawing and Scrolling Nametable Helpers
        private void EndScanline(ref ushort vReg)
        {
            //are we at the end of the y-scroll ?
            if ((vReg & 0x7000) == 0x7000)
            {
                //reset the y-scroll to 0
                vReg &= 0x8FFF;

                //are we at tile reference 29 ?
                if ((vReg & 0x03E0) == 0x03A0)
                {
                    //if so switch the nametables
                    vReg ^= 0x0800;

                    //set the vertical tile row to 0
                    vReg &= 0xFC1F;
                }
                else
                {
                    //did we write some odd value to vram address during non-vblank time
                    if ((vReg & 0x03E0) == 0x03E0)
                    {
                        //we reset vertical tile row to 0
                        //but we don't toggle name tables
                        vReg &= 0xFC1F;
                    }
                    else
                    {
                        //we move to the next tile row in the nametable
                        vReg += 0x0020;
                    }
                }
            }
            else
            {
                //move to the next vertical tile offset
                vReg += 0x1000;
            }
        }

        private void EndTile(ref ushort vReg)
        {
            //are we at the last tile in the row (31)
            if ((vReg & 0x001F) == 0x001F)
            {
                //switch horizontal nametable
                vReg ^= 0x0400;

                //reset tile column to 0
                vReg &= 0xFFE0;
            }
            else
            {
                vReg++;
            }
        }

        private void EndPixel(ref ushort vReg, ref byte x)
        {
            //are we at the next tile ?
            if ((x & 0x07) == 0x07)
            {
                EndTile(ref vReg);
                x = 0;
            }
            else
            {
                x++;
            }
        }

        private void FrameStart()
        {
            //start of new frame
            if (IsSpritesVisible && IsBackgroundVisible)
            {
                _vramAddressRegister = _vramAddressRegisterTemp;
            }

            //clear the vblank flag and sprite flag register (we are starting a new frame now)
            _ppuStatus &= 0x3F;

            //we'll cache the current palette, hoping this doesn't cause any issues
            //cache BG
            for (int i = 0; i < 0x0F; i++)
            {
                _bgCache[i] = GetPixelColor(NESPalette.Background, (byte)i);
            }

            //cache SP
            for (int i = 0; i < 0x0F; i++)
            {
                _spCache[i] = GetPixelColor(NESPalette.Sprite, (byte)i);
            }

            //recreate the frame buffer to empty all contents
            CreateFrameBuffer();
        }
        #endregion

        private void StartVBlank()
        {
            //set the vblank flag (we are entering vblank now)
            _ppuStatus |= 0x80;
        }

        private void EndVBlank()
        {
            //clear the vblank flag and sprite flag register (we are finished vblank period)
            _ppuStatus &= 0x3F;
        }

        //the ppu draw frame function controls the 
        //general flow of execution
        public void DrawFrame()
        {
            //star the frame
            FrameStart();

            PerfMonitor.Current.Mark(PerfItems.FillScanlines);

            for (uint i = 0; i < NES_SCREEN_HEIGHT; i++)
            {
                _currentScanline = i;
                
                //execute a certain number of cycles for each scanline
                PerfMonitor.Current.Mark(PerfItems.ExecuteScanlineCycles);
                _parent.CPU.Execute(EXECUTE_CYCLES_PER_SCANLINE);
                PerfMonitor.Current.Measure(PerfItems.ExecuteScanlineCycles);

                PerfMonitor.Current.Mark(PerfItems.DrawScanline);
                DrawScanline(true);
                PerfMonitor.Current.Measure(PerfItems.DrawScanline);
            }

            PerfMonitor.Current.Measure(PerfItems.FillScanlines);

            //execute another single line work of statement + 1
            _parent.CPU.Execute(EXECUTE_CYCLES_PER_SCANLINE + 1);

            //now start the vblank period
            StartVBlank();

            //now raise nmi (only happens if nmi bit in control reg 0 is enabled)
            PerfMonitor.Current.Mark(PerfItems.DoNMI);
            _parent.CPU.DoNMI();
            PerfMonitor.Current.Measure(PerfItems.DoNMI);

            //now emulate the cpu cycles during vblank which is cycles per scanline
            //for 20 vblank lines
            PerfMonitor.Current.Mark(PerfItems.ExecuteVBlankCycles);
            _parent.CPU.Execute(EXECUTE_CYCLES_PER_SCANLINE * VBLANK_SCANLINE_SIMUALTION);
            PerfMonitor.Current.Measure(PerfItems.ExecuteVBlankCycles);

            //now end vblank period
            EndVBlank();
        }

        public override string ToString()
        {
            return "PPU";
        }

        private NESPixel GetPixelColor(NESPalette palette, byte paletteAddr)
        {
            return _nesPalette.GetPaletteColor(_nesPalette.GetPaletteEntry(palette, paletteAddr));
        }

        private void DrawBG()
        {
            byte currentBgTileIndex;
            byte currentNT;
            byte tileRow, tileCol;
            byte paletteMSB = 0;
            byte paletteAddr = 0;
            byte yscrollOffset = 0;
            uint pixel = 0;
            int tilePixel = 0;
            uint startPixel = 0;
            uint subPixel = 0;
            byte lsbByte = 0, msbByte = 0;
            int shift = 0;
            int i = 0;
            Mappers.Mapper mapper = Emulator.CurrentCartridge.MapperObject;

            //set the start value for starting pixel
            startPixel = (_currentScanline * NES_SCREEN_WIDTH);

            while (i++ < NameTable.NAME_TABLE_COLS)
            {
                //draw each tile as an unrolled loop
                currentNT = (byte)((_vramAddressRegister & 0x0C00) >> 10);
                tileCol = (byte)(_vramAddressRegister & 0x001F);
                tileRow = (byte)((_vramAddressRegister & 0x03E0) >> 5);
                yscrollOffset = (byte)((_vramAddressRegister & 0x7000) >> 12);

                currentBgTileIndex = _nameTables.VRAM[currentNT].NameTableBytes[(tileRow * NameTable.NAME_TABLE_COLS) + tileCol];

                lsbByte = mapper.Read((uint)((currentBgTileIndex * PatternTable.TILE_BYTE_LENGTH) + yscrollOffset + (uint)_bgTable.CurrentStartAddress));
                msbByte = mapper.Read((uint)((currentBgTileIndex * PatternTable.TILE_BYTE_LENGTH) + yscrollOffset + 8 + (uint)_bgTable.CurrentStartAddress));

                paletteMSB = _nameTables.GetTileColorMSB(currentNT, tileRow, tileCol);

                tilePixel = 0;
                subPixel = startPixel * 4;

                //unrolled tile pixel settings (for perf)
                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;  
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);

                shift = 7 - tilePixel++;
                paletteAddr = (byte)(((lsbByte >> (shift)) & 0x01) | (((msbByte >> (shift)) & 0x01) << 1) | (paletteMSB << 2));
                _frameBufferInfo[startPixel++] |= 0x01;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].R; _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].G;
                _frameBufferBytes[subPixel++] = _bgCache[paletteAddr].B; _frameBufferBytes[subPixel++] = 255;
                EndPixel(ref _vramAddressRegister, ref _x);
            }
        }

        private void DrawSprites()
        {
            //need to author the sprite rendering code here
            int i = 0;
            byte renderedSprites = 0;
            byte spriteHeight = 0;
            byte renderStartX = 0, renderEndX = 0;
            byte renderY = 0;
            byte temp = 0;
            byte[] currentTile;
            int patternTableIndex = 0;
            PatternTable tilePatternTable = null;
            int direction = 1;
            byte localPixelIndex = 0;
            uint globalPixelIndex = 0;
            uint globalPixelSubIndex = 0;
            byte paletteAddr = 0;
            byte lsbByte = 0, msbByte = 0;
            NESPixel tempPixel = null;
            SpriteInfo currentSprite = null;

            //render sprites backwards to ensure priority order is kept
            //NOTE: doing this could be dangerous because of the 8 sprites
            //per scanline limitation, rather keep status of sprite buffer
            //rendering and render forwards to ensure no loss of information
            while (i < (((int)SpriteRam.MAX_SPRITES) - 1))
            {
                currentSprite = _spriteRam.GetSpriteInfo((byte)i);
                spriteHeight = (byte)((((byte)_spriteRam.SpriteSize) + 1) * PatternTable.TILE_HEIGHT);

                if ((currentSprite.SpriteY + (spriteHeight-1) < _currentScanline) ||
                    (currentSprite.SpriteY > _currentScanline))
                {
                    //This sprite is not in this scanline
                    i++;
                    continue;
                }

                renderedSprites++;

                if (renderedSprites >= SpriteRam.MAX_SPRITES_PER_SCANLINE)
                {
                    //do we in the future allow this limitation to be broken ?
                    break;
                }

                renderStartX = 0;
                renderEndX = 8;

                //Clip the sprite to screen co-ords (width)
                if (currentSprite.SpriteX + (((int)PatternTable.TILE_WIDTH) - 1) > ((int)NES_SCREEN_WIDTH-1))
                {
                    renderEndX = (byte)((NES_SCREEN_WIDTH) - currentSprite.SpriteX);
                }

                if (ShouldClipSpritesLeft &&
                    currentSprite.SpriteX < PatternTable.TILE_WIDTH)
                {
                    //if we start at beginning of line and left8 clipping is
                    //on nothing will render
                    if (renderStartX == 0)
                        continue;

                    //move the start pointer forward count of pixels hidden in left 8 clipping so we 
                    //start rendering the sprite from the point where it's pixels are visible again.
                    renderStartX += (byte) (((int)PatternTable.TILE_WIDTH-1) - currentSprite.SpriteX);
                }

                renderY = (byte)(((int)_currentScanline) - currentSprite.SpriteY);

                if (currentSprite.FlipHorizontal)
                {
                    temp = renderEndX;
                    renderEndX = (byte)((int)renderStartX - 1);
                    renderStartX = (byte)(temp - 1);
                    direction = -1;
                }

                if (currentSprite.FlipVertical)
                {
                    renderY = (byte)(((int)spriteHeight - (int)renderY) - 1);
                }

                //used to reference final pt index for 8x16 translation common code
                patternTableIndex = currentSprite.PatternTableIndex;

                //generally sprite pattern table is known but is only overriden
                //by the 8x16 pattern table code.

                //currently we take the view that the sprite and background pattern
                //tables are seperate. I'd like to keep this view, but we need some
                //special logic here to handle 8x16 sprites, not too much magic though
                GetTableAndIndexForSpriteTile(currentSprite, renderY, out tilePatternTable, out patternTableIndex);

                //grab the tile before the scanline render loop starts (we won't need to switch
                //during as we render only a single scanlines horizontally and sprites are only
                //allowed a single tile horizontally
                currentTile = tilePatternTable.GetTileAt((byte)patternTableIndex);

                for (int j = (int)renderStartX; j != renderEndX; j += direction)
                {
                    //the main sprite rendering loop

                    //we don't bother to attempt to render the current pixel
                    //if the lower 2 bits of the pattern table entry are 0
                    //This is because the pattern table entry is then a multiple
                    //of 4 which is just a mirror of the background color according
                    //to the standard PPU memmap
                    localPixelIndex = (byte)((renderY * PatternTable.TILE_WIDTH) + j);
                    globalPixelIndex = (uint)(((currentSprite.SpriteY + renderY) * NES_SCREEN_WIDTH) + (currentSprite.SpriteX + j));
                    globalPixelSubIndex = globalPixelIndex * 4;

                    //if not just a mirror of the background color and the current pixel has been already drawn on by a sprite
                    if ((currentTile[localPixelIndex] & 0x03) != 0x00 && ((_frameBufferInfo[globalPixelIndex] & 0x02) != 0x02))
                    {
                        paletteAddr = (byte)((currentSprite.ColorPaletteEntryMSB << 2) | currentTile[localPixelIndex]);

                        //if the current pixel has been written by the background
                        //and this is sprite 0, and we are about to draw sprite to
                        //it, and we haven't yet set the hit flag, then set it.
                        if (i == 0 
                            && ((_frameBufferInfo[globalPixelIndex] & 0x01) == 0x01)
                            && !IsSprite0Hit)
                        {
                            IsSprite0Hit = true;
                        }

                        if (currentSprite.BackgroundHasPriority)
                        {
                            //regardless of whether I draw or not to this pixel
                            //no other sprite can draw to this pixel now since they
                            //are lower pri
                            _frameBufferInfo[globalPixelIndex] |= 0x02;

                            if ((_frameBufferInfo[globalPixelIndex] & 0x01) != 0x01)
                            {
                                tempPixel = _spCache[IsColorDisplay ? paletteAddr : (byte)(paletteAddr & 0xF0)];
                                _frameBufferBytes[globalPixelSubIndex++] = tempPixel.R; _frameBufferBytes[globalPixelSubIndex++] = tempPixel.G;
                                _frameBufferBytes[globalPixelSubIndex++] = tempPixel.B; _frameBufferBytes[globalPixelSubIndex++] = 255;
                            }

                        }
                        else
                        {
                            if ((_frameBufferInfo[globalPixelIndex] & 0x02) != 0x02)
                            {
                                tempPixel = _spCache[IsColorDisplay ? paletteAddr : (byte)(paletteAddr & 0xF0)];
                                _frameBufferBytes[globalPixelSubIndex++] = tempPixel.R; _frameBufferBytes[globalPixelSubIndex++] = tempPixel.G;
                                _frameBufferBytes[globalPixelSubIndex++] = tempPixel.B; _frameBufferBytes[globalPixelSubIndex++] = 255;
                                _frameBufferInfo[globalPixelIndex] |= 0x02;
                            }
                        }
                    }
                }

                i++;
            }

            if (renderedSprites > SpriteRam.MAX_SPRITES_PER_SCANLINE)
            {
                //we lost sprites during this scanline, set bit 5 of status regs
                _ppuStatus |= 0x20;
            }
            else
            {
                //no lost sprites unset bit 5 of status regs
                _ppuStatus &= 0xDF;
            }
        }

        /// <summary>
        /// Translates the pattern table and pattern table index for 8x16 sprites
        /// basically hiding the complexity of 8x16 sprite handling from the user
        /// </summary>
        /// <param name="sprite">Current sprite info to use</param>
        /// <param name="Y">Y offset into sprite being rendered</param>
        /// <param name="table">Pattern Table to use (out)</param>
        /// <param name="index">Pattern Table Index to use (out)</param>
        private void GetTableAndIndexForSpriteTile(SpriteInfo sprite, byte Y, out PatternTable table, out int index)
        {
            //used to reference final pt index for 8x16 translation common code
            index = sprite.PatternTableIndex;

            //init the 
            table = _spriteTable;

            //generally sprite pattern table is known but is only overriden
            //by the 8x16 pattern table code.

            //currently we take the view that the sprite and background pattern
            //tables are seperate. I'd like to keep this view, but we need some
            //special logic here to handle 8x16 sprites, not too much magic though
            if (_spriteRam.SpriteSize == SpriteSizeOption.Sprite8x16)
            {
                //are we handling 8x16 sprites? 
                //here is where some of the logic falls apart for
                //seperate pattern tables (it is minor though)
                if ((sprite.PatternTableIndex & 0x01) == 0x01)
                {
                    //we are odd get our info from address 0x1000
                    table = GetPatternTableForSelection(PatternTableSelection.Table1);

                    //we use the top 7 bits from this value * 2, effectively subtracting 1 from
                    //pt index gets us there, this gives us index of the top tile in 8x16 sprite
                    index -= 1;
                }
                else
                {
                    //we are even get our info from address 0x0000
                    table = GetPatternTableForSelection(PatternTableSelection.Table0);

                    //top 7 bits are used as index * 2 and we add 1 to this to get final tile to use
                    //this gives us the top tile in 8x16 sprite
                    index += 1;
                }

                //are we rendering second tile of sprite, if yes then we need to inc pt index
                if (Y >= PatternTable.TILE_HEIGHT)
                {
                    index++;
                }
            }
        }

        private PatternTable GetPatternTableForSelection(PatternTableSelection selection)
        {
            if (_bgTable.CurrentStartAddress == selection)
            {
                return _bgTable;
            }
            else
            {
                return _spriteTable;
            }
        }

        public bool IsVisible
        {
            get
            {
                return (IsBackgroundVisible || IsSpritesVisible);
            }
        }

        private void DrawScanline(bool doDraw)
        {
            int y = 0;

            if (!IsBackgroundVisible)
            {
                y = (int)((_currentScanline * NES_SCREEN_WIDTH) * 4);

                //set entire buffer to bg color
                for (int j = 0; j < NES_SCREEN_WIDTH; j++)
                {
                    _frameBufferBytes[y + (j)] = _nesPalette.BackgroundColor.R; _frameBufferBytes[y + (j + 1)] = _nesPalette.BackgroundColor.G;
                    _frameBufferBytes[y + (j + 2)] = _nesPalette.BackgroundColor.B; _frameBufferBytes[y + (j + 3)] = 255;
                }
            }

            //in order to start drawing anything at least backgrounds or sprites should be
            //enabled, otherwise we're likely waiting for cartridge to load initial data
            if (IsBackgroundVisible || IsSpritesVisible)
            {
                StartScanline(ref _vramAddressRegister, ref _vramAddressRegisterTemp);

                //first draw the background complete
                //then start drawing sprites
                //we can't really try to draw sprites
                //during background tile layout
                //because sprite layout is different and 
                //could get overwritten
                PerfMonitor.Current.Mark(PerfItems.DrawBG);
                if (IsBackgroundVisible)
                {
                    DrawBG();
                }
                PerfMonitor.Current.Measure(PerfItems.DrawBG);

                //Now draw all the sprites
                if (IsSpritesVisible)
                {
                    PerfMonitor.Current.Mark(PerfItems.DrawSprite);
                    DrawSprites();
                    PerfMonitor.Current.Measure(PerfItems.DrawSprite);
                }

                EndScanline(ref _vramAddressRegister);
            }
        }
    }
}
