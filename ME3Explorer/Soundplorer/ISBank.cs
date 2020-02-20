﻿using Gammtek.Conduit.Extensions.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Gammtek.Conduit.IO;

namespace ME3Explorer.Soundplorer
{
    class ISBank
    {
        private string Filepath;
        public List<ISBankEntry> BankEntries = new List<ISBankEntry>();
        public ISBank(string isbPath)
        {
            Filepath = isbPath;
            ParseBank(new EndianReader(new MemoryStream(File.ReadAllBytes(isbPath))), false);
        }

        public ISBank(byte[] binData, bool isEmbedded)
        {
            MemoryStream ms = new MemoryStream(binData);
            ParseBank(new EndianReader(ms), isEmbedded);
        }

        private void ParseBank(EndianReader ms, bool isEmbedded)
        {
            int numEntriesWithData = 1;
            //long dataStartPosition = ms.Position;
            //string shouldBeRiff = ms.ReadString(4, false);
            //if (shouldBeRiff != "RIFF")
            //{
            //    Debug.WriteLine("Not a RIFF!");
            //}
            //uint riffSize = ms.ReadUInt32();
            //var riffType = ms.ReadString(8, false); //technically not type, its just how this file format works
            //ISBankEntry isbEntry = null;
            //uint blocksize = 0;
            //int currentCounter = counter;
            //bool endOfFile = false;

            //if (isEmbedded && riffType != "isbftitl")
            //{
            //    //its an icbftitl, which never has data.
            //    ms.Seek(riffSize - 8, SeekOrigin.Current); //skip it
            //}
            //else
            //{
            //    //get full data
            //    var pos = ms.Position;
            //    ms.Position -= 8; //go back 8
            //    var fulldata = new byte[riffSize + 8];
            //    ms.Read(fulldata, 0, fulldata.Length);
            //    ms.Position = pos; //reset
            //    isbEntry = new ISBankEntry(); //start of a new file
            //    isbEntry.FullData = fulldata;

            //    blocksize = ms.ReadUInt32(); //size of isfbtitl chunk
            //    ms.Seek(blocksize, SeekOrigin.Current); //skip it
            //}
            ////todo change to this
            ////  while AudioFile.Position <> BundleReader.OffsetsArray[FileNo] + BundleReader.FileSizesArray[FileNo] do
            uint chunksize = 0;
            ISBankEntry isbEntry = null;
            while (ms.BaseStream.Position < ms.BaseStream.Length)
            {
                chunksize = 0; //reset
                var chunkStartPos = ms.BaseStream.Position;
                string blockName = ms.ReadStringASCII(4);
                //Debug.WriteLine(blockName + " at " + (ms.Position - 4).ToString("X8"));
                switch (blockName)
                {
                    case "LIST":
                        chunksize = ms.ReadUInt32();
                        var nextblockname = ms.ReadStringASCII(4);
                        var nextblockname2 = ms.ReadStringASCII(4);
                        if (nextblockname == "samp" && nextblockname2 == "titl")
                        {
                            if (!isEmbedded)
                            {
                                //upcoming sample data
                                //add old ISB entry
                                if (isbEntry?.DataAsStored != null)
                                {
                                    BankEntries.Add(isbEntry);
                                }

                                isbEntry = new ISBankEntry();
                            }
                        }
                        else
                        {
                            //maybe isb container, ignore
                            ms.BaseStream.Position = chunksize + 8 + chunkStartPos;
                            //Debug.WriteLine($"Skipping non-sample LIST at 0x{chunkStartPos:X8}");
                            continue;
                        }

                        chunksize = ms.ReadUInt32(); //size of block
                        string tempStr = ""; //we have to build it manually because of how they chose to store it in a weird non-ASCII/unicode way
                        bool endOfStr = false;
                        for (int i = 0; i < chunksize / 2; i++)
                        {
                            short value = ms.ReadInt16();
                            if (value != 0 && !endOfStr)
                            {
                                tempStr += (char)value;
                            }
                            else
                            {
                                //used to skip the rest of the block
                                endOfStr = true;
                            }
                        }
                        isbEntry.FileName = tempStr;
                        break;
                    case "sinf":
                        chunksize = ms.ReadUInt32();
                        var pos = ms.BaseStream.Position;
                        ms.ReadInt64(); //skip 8
                        isbEntry.sampleRate = ms.ReadUInt32();
                        isbEntry.pcmBytes = ms.ReadUInt32();
                        isbEntry.bps = ms.ReadInt16();
                        ms.BaseStream.Position = pos + chunksize; //skip to next chunk
                        break;
                    case "chnk":
                        ms.Seek(4, SeekOrigin.Current);
                        isbEntry.numberOfChannels = ms.ReadUInt32();
                        break;
                    case "cmpi":
                        //Codec/compression index
                        var size = ms.ReadInt32();
                        pos = ms.BaseStream.Position;
                        isbEntry.CodecID = ms.ReadInt32();
                        isbEntry.CodecID2 = ms.ReadInt32();
                        ms.BaseStream.Position = pos + size;
                        break;
                    case "data":
                        numEntriesWithData++;
                        chunksize = ms.ReadUInt32(); //size of block
                        isbEntry.DataOffset = (uint)ms.BaseStream.Position;
                        MemoryStream data = new MemoryStream();
                        ms.BaseStream.CopyToEx(data, (int)chunksize);
                        data.Position = 0;
                        var str = data.ReadString(4, false);
                        isbEntry.DataAsStored = data.ToArray();
                        break;
                    case "FFIR":
                    case "RIFF":
                        if (blockName == "FFIR" && ms.BaseStream.Position == 0x4)
                        {
                            ms.Endian = Endian.Big;
                        }
                        if (isEmbedded)
                        {
                            //EMBEDDED ISB
                            //this is the start of a new file.
                            var riffSize = ms.ReadUInt32(); //size of isfbtitl chunk
                            var riffType = ms.ReadStringASCII(4); //type of ISB riff
                            var riffType2 = ms.ReadStringASCII(4); //type of ISB riff
                            if (riffType != "isbf" && riffType2 == "titl")
                            {
                                //its an icbftitl, which never has data.
                                ms.Seek(riffSize - 8, SeekOrigin.Current); //skip it
                                continue; //skip
                            }

                            //add old ISB entry
                            if (isbEntry?.DataAsStored != null)
                            {
                                BankEntries.Add(isbEntry);
                            }

                            isbEntry = new ISBankEntry();
                            isbEntry.FileEndianness = ms.Endian;
                            isbEntry.FullData = new byte[riffSize + 8];
                            pos = ms.BaseStream.Position;
                            ms.BaseStream.Position = ms.BaseStream.Position - 16;
                            ms.Read(isbEntry.FullData, 0, (int)riffSize + 8);
                            ms.BaseStream.Position = pos;
                            chunksize = ms.ReadUInt32(); //size of isfbtitl chunk
                            ms.Seek(chunksize, SeekOrigin.Current); //skip it
                        }
                        else
                        {
                            //ISB file - has external RIFF header and samptitl's separating each data section
                            var riffSize = ms.ReadUInt32(); //size of isfbtitl chunk
                            var riffType = ms.ReadStringASCII(4); //type of ISB riff
                            var riffType2 = ms.ReadStringASCII(4); //type of ISB riff
                            if (riffType != "isbf" && riffType2 != "titl")
                            {
                                //its an icbftitl, which never has data, or is not ISB
                                ms.Seek(riffSize - 8, SeekOrigin.Current); //skip it
                                continue; //skip
                            }

                            //can't do += here it seems
                            ms.Seek(ms.ReadInt32(), SeekOrigin.Current); //skip this title section

                            //we will continue to parse through ISB header until we find a LIST object for sample data
                        }

                        break;
                    default:
                        //skip the block
                        chunksize = ms.ReadUInt32(); //size of block
                        ///sDebug.WriteLine($"Skipping block {blockName} of size {chunksize} at 0x{ms.Position:X8}");
                        ms.Seek(chunksize, SeekOrigin.Current); //skip it
                        break;
                }
                if (chunksize % 2 != 0)
                {
                    ms.Seek(1, SeekOrigin.Current); //byte align
                }
            }
            if (isbEntry?.DataAsStored != null)
            {
                BankEntries.Add(isbEntry);
            }
        }
    }
}