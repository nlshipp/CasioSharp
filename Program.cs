using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CasioSharp
{
    enum Mode
    {
        NOWAIT = 0,
        WAIT = 1
    }

    struct CasioHeader
    {
        public int marker;        /* ':' marks the start of a record              */
        public int nbytes;        /* 2 digit ASCII, # databytes                   */
        public int type;          /* 2 digit ASCII, record type                   */
        public int low;           /* 2 digit ASCII, low memory address            */
        public int high;          /* 2 digit ASCII, high memory address           */
    }

    struct Header
    {
        public int nbytes;        /* number of databytes in the record            */
        public int type;          /* record type                                  */
        public uint address;       /* load address in memory                       */
    }


    static class Program
    {
        const bool debug = true;
        const bool debug2 = false;
        const bool NOCHECK = false;

        static int LastRead;
        static int WriteStatus = 0;

        const int TIMEOUT = 60500;

        const int PRINT = 2;
        const int READ  = 1;
        const int WRITE = 0;

        const char CR = '\r';
        const char LF = '\n';
        const char XON = (char)0x11;
        const char XOFF = (char)0x13;
        const char ACK = (char)0x23;
        const char NACK = (char)0x3f;
        const char STOP = (char)0x21;
        
        static SerialPort port;
        static BinaryWriter outFile;
        static BinaryReader inFile;

        static bool Stopped = false;
        static int Direction = READ;

        static UInt32 Record;
        static int blkflg;

        static byte[] DataBuffer = new byte[512];
        static byte[] WBuffer = new byte[1024];

        static byte[] tempBuffer = new byte[1024];

        static char ch;
        static byte nbytes;

        static CasioHeader CHeader;
        static Header MHeader;

        static void Main(string[] args)
        {
            Console.WriteLine("Casio# reader.");
            if (args.Length != 2)
            {
                PrintUsage();
                return;
            }

            switch (args[0].ToLower())
            {
                case "-r":
                    Direction = READ;
                    break;

                case "-p":
                    Direction = PRINT;
                    break;

                case "-w":
                    Direction = WRITE;
                    break;

                default:
                    PrintUsage();
                    return;
            }

            if (Direction == PRINT)
            {
                inFile = new BinaryReader(new FileStream(args[1], FileMode.Open, FileAccess.Read));

                while (!Stopped)
                {
                    ReadFile();
                    DisplayStatus();
                }
                return;
            }

            if (Direction == WRITE)
            {
                return;
            }


            port = new SerialPort("COM15", 4800);
            port.Parity = Parity.None;
            port.StopBits = StopBits.One;
            port.DataBits = 8;
            port.Handshake = Handshake.None;  // should be XONXOFF?
            port.RtsEnable = false; // RTS -12v
            port.DtrEnable = true;  // DTR +12v
            port.ReadTimeout = SerialPort.InfiniteTimeout;
            port.WriteTimeout = SerialPort.InfiniteTimeout;
            port.Encoding = Encoding.ASCII;
            
            port.Open();

            outFile = new BinaryWriter(new FileStream(args[0], FileMode.OpenOrCreate, FileAccess.Write));
                        
            while (!Stopped)
            {
                WaitForCasio();

                while (!Stopped)
                {
                    ReadLine();
                    DisplayStatus();

                    // write out header in little endian format
                    outFile.Write((byte)MHeader.nbytes);
                    outFile.Write((byte)MHeader.type);
                    outFile.Write((short)MHeader.address);

                    // write out buffer and include checksum
                    outFile.Write(DataBuffer, 0, MHeader.nbytes + 1);
                }
            }

            outFile.Close();
        }

        static void WaitForCasio()
        {
            int i;

            if (Direction == READ)
            {
                while (!Stopped)
                {
                    do
                    {
                        i = ReadByte(Mode.WAIT);
                        if (i < 0)
                        {
                            Console.WriteLine("ReadByte returned {0}", i);
                            Terminate();
                        }
                    }
                    while (i != CR && !Stopped);

                    if (Stopped)
                    {
                        Terminate();
                    }

                    i = ReadByte(Mode.WAIT);

                    if (i == LF)
                    {
                        WriteByte(XON);
                        WriteStatus = XON;
                        return;
                    }
                }
            }
            else
            {
                throw new Exception("Unimplemented");
            }
        }

        /*
         * R e a d H e a d e r
         *
         *      Read a record header from the CASIO
         */

        static void ReadHeader()
        {
            /*
             * Wait until ':'
             */
            if (debug2)
                Console.Write("\nReadheader waiting for a :\n");

            int count = 0;
            do
            {
                CHeader.marker = ReadByte(Mode.WAIT);
                tempBuffer[count] = (byte)CHeader.marker;
                count++;

                // Casio was paused waiting for user to select next item, send XON to resume?
                if (count > 1 && CHeader.marker == LF && tempBuffer[count-2] == CR)
                {
                    WriteByte(XON);
                    count = 0;
                    Console.Write(".");
                }
            }
            while (CHeader.marker != ':' && !Stopped);
            if (debug2)
                Console.Write("\nReadheader got a :\n");
            if (debug)
            {
                if (count > 1)
                    Console.Write("\nExtra data before header: '{0}'\n", Encoding.ASCII.GetString(tempBuffer, 0, count));
            }

            CHeader.nbytes = ((ReadByte(Mode.WAIT) & 0xff) << 8) |
              (ReadByte(Mode.WAIT) & 0xff);

            if (debug2)
                Console.Write("\nReadheader got nbytes {0:x} \n", CHeader.nbytes);

            CHeader.type = ((ReadByte(Mode.WAIT) & 0xff) << 8) |
              (ReadByte(Mode.WAIT) & 0xff);

            if (debug2)
                Console.Write("\nReadheader got type {0:x} \n", CHeader.type);

            CHeader.low = ((ReadByte(Mode.WAIT) & 0xff) << 8) |
              (ReadByte(Mode.WAIT) & 0xff);

            if (debug2)
                Console.Write("\nReadheader got low {0:x} \n", CHeader.low);

            CHeader.high = ((ReadByte(Mode.WAIT) & 0xff) << 8) |
              (ReadByte(Mode.WAIT) & 0xff);

            if (debug2)
                Console.Write("\nReadheader got high {0:x} \n", CHeader.high);

            /*
             * Casio header is read, now translate it
             * into something useful
             */
            MHeader.nbytes = (atoh(CHeader.nbytes >> 8) << 4) |
              atoh(CHeader.nbytes & 0xff);

            if (debug2)
                Console.Write("\nReadheader translated nbytes {0:x} \n", MHeader.nbytes);

            MHeader.type = (atoh(CHeader.type >> 8) << 4) |
              atoh(CHeader.type & 0xff);

            if (debug2)
                Console.Write("\nReadheader: frame type {0:x} \n", MHeader.type);

            // Casio doesn't update high byte when records are larger than 256 bytes, so kind of useless.
            MHeader.address = (uint)(atoh(CHeader.high >> 8) << 4) |
              atoh(CHeader.high & 0xff);

            MHeader.address *= 256;

            MHeader.address += (uint)(atoh(CHeader.low >> 8) << 4) |
              atoh(CHeader.low & 0xff);

            if (debug2)
                Console.Write("\nReadheader translated address {0:x} \n", MHeader.address);

        }

        /*
         * R e a d L i n e
         *
         *      Read one complete record from the CASIO
         */

        static void ReadLine()
        {
            /*
             * Read the CASIO record header
             */
            if (debug2)
                Console.Write("calling readheader\n");

            ReadHeader();

            if (debug2)
                Console.Write("\nDone ReadHeader\n");

            if (debug)
            {
                Console.Write("\nMHeader bytes:{0} type:{1} address:0x{2:X4}\n", MHeader.nbytes, MHeader.type, MHeader.address);
            }

            if (MHeader.nbytes == 0x00)
            {
                if (debug)
                    Console.Write("\nsaw last data frame\n");
                if (MHeader.address == 0x0100)
                {
                    if (debug)
                        Console.Write("\nsending ack to continue transmission \n");
                    WriteByte(ACK);
                }
            }
            /*
             * Read the data bytes in the record
             */
            for (nbytes = 0; nbytes < MHeader.nbytes && !Stopped; nbytes++)
            {
                DataBuffer[nbytes] = ReadHex();

                if (debug2)
                    Console.Write("\nDatabytes --->  {0:x}\n", DataBuffer[nbytes]);

                /* store the databyte into the data file */
//                fprintf(data, "%c", DataBuffer[nbytes]);
            }
//            fprintf(data, "\n");

            /*
             * Read the checksum
             */
            DataBuffer[nbytes++] = ReadHex();

            if (debug2)
                Console.Write("\nDone readline\n");
        }

        static void ReadFile()
        {
            // Read Header and data bytes in little endian format
            MHeader.nbytes = inFile.ReadByte();
            MHeader.type = inFile.ReadByte();
            MHeader.address = inFile.ReadUInt16();

            inFile.ReadBytes(MHeader.nbytes + 1).CopyTo(DataBuffer, 0);
        }

        /*
         * W r i t e L i n e
         *
         *      Write one complete record from
         *      the file to the CASIO
         */
#if false
        static void WriteLine(FILE* infile)
        {
            int i;
            /*
             * Read the next record header from the file
             */
            if (fread(&MHeader, sizeof(MHeader), 1, infile) != 1)
            {
                Stopped = true;
                return;
            }

            /*
             * Read the databytes and the checksum from the file
             */
            fread(DataBuffer, MHeader.nbytes + 1, 1, infile);

            /*
             * Convert record to something for CASIO
             */
            WBuffer[0] = ':';
            WBuffer[1] = htoa(MHeader.nbytes >> 4);
            WBuffer[2] = htoa(MHeader.nbytes);
            WBuffer[3] = htoa(MHeader.type >> 4);
            WBuffer[4] = htoa(MHeader.type);
            WBuffer[5] = htoa(MHeader.address >> 4);
            WBuffer[6] = htoa(MHeader.address);
            WBuffer[7] = htoa(MHeader.address >> 12);
            WBuffer[8] = htoa(MHeader.address >> 8);

            for (i = 0; i < MHeader.nbytes + 1; i++)
            {
                WBuffer[2 * i + 9] = htoa(DataBuffer[i] >> 4);
                WBuffer[2 * i + 10] = htoa(DataBuffer[i] & 0x0f);
            }

            WriteBuffer(2 * MHeader.nbytes + 11);

            /*
             * Wait for reply
             */
            if (MHeader.nbytes == 0 && MHeader.address == 0x0100)
            {
                while (!Stopped)
                {
                    i = ReadByte(Mode.WAIT);

                    switch (i)
                    {
                        case NACK:
                            Console.Write("\nTransmission error, stopped\n");
                            Stopped = true;
                            WriteByte(STOP);
                            break;

                        case ACK:
                            return;

                        case STOP:
                        case XON:
                        case XOFF:
                            return;

                        default:
                            Console.Write("0x%02x\n", i);
                            return;
                    }
                }
            }
        }
        /*
         * W r i t e B u f f e r
         *
         *      Write the data in the CASIO Write Buffer
         *      to the CASIO
         */

        static void WriteBuffer(int num)
        {
            int i;

            for (i = 0; i < num; i++)
            {
                WriteByte(WBuffer[i]);
            }

        }

        /*
         * U p p e r C a s e
         *
         *  Turn a string into all uppercase characters
         */

        static void UpperCase(char* s)
        {
            while (*s)
            {
                if (*s >= 'a' && *s <= 'z')
                {
                    *s = *s - 'a' + 'A';
                }

                s++;
            }
        }

#endif

        /*
         * R e a d H e x
         *
         *      Read two ASCII digits from the COM port
         *      and convert these into a hexadecimal value
         */

        static byte ReadHex()
        {
            return (byte)((atoh(ReadByte(Mode.WAIT)) << 4) | (atoh(ReadByte(Mode.WAIT))));
        }

        /*
         * a t o h
         *
         *      Convert an ASCII number to a hexadecimal value
         */

        static byte atoh(int c)
        {
            if (c >= '0' && c <= '9')
            {
                return (byte)(c - '0');
            }

            if (c >= 'a' && c <= 'f')
            {
                return (byte)(c - 'a' + 10);
            }

            if (c >= 'A' && c <= 'F')
            {
                return (byte)(c - 'A' + 10);
            }

            return (0);
        }

        /*
         * h t o a
         *
         *      Convert a hexadecimal value to an ASCII number
         */

        static char htoa(byte c)
        {
            c &= 0xf;

            if (c <= 9)
            {
                return (char)(c + '0');
            }

            return (char)(c - 10 + 'A');
        }

        /*
         * D i s p l a y S t a t u s
         */
        static void DisplayStatus()
        {
            if (debug2)
            {
                Console.Write("\n Display status: Mheader,nbytes {0}\n", MHeader.nbytes);
                Console.Write("\n Display status: DatadBuffer[0] {0}\n", DataBuffer[0]);
            }
            if (MHeader.nbytes == 0x02 && DataBuffer[1] == 0x00)
            {
                switch (DataBuffer[0])
                {
                    case 0x90:
                        {
                            Console.Write("\nPhone:         ");
                            if (debug)
                                Console.Write("\n Data ==> phone\n");
//                            fprintf(data, "\n===================== > phone < ==========\n");
                            Record = 0;
                            break;
                        }
                    case 0xa0:
                        {
                            Console.Write("\nMemo:          ");
                            if (debug)
                                Console.Write("\n Data ==> memo\n");
//                            fprintf(data, "\n===================== > MEMO < =============\n");
                            Record = 0;
                            break;
                        }
                    case 0x91:
                        {
                            Console.Write("\nReminder:      ");
                            if (debug)
                                Console.Write("\n Data ==> reminder\n");
//                            fprintf(data, "\n===================== > REMINDER < =============\n");
                            Record = 0;
                            break;
                        }
                    case 0xb0:
                        {
                            Console.Write("\nSchedule:      ");
                            if (debug)
                                Console.Write("\n Data ==> schedule\n");
//                            fprintf(data, "\n===================== > SCHEDULE < =============\n");
                            Record = 0;
                            break;
                        }
                    case 0x80:
                        {
                            Console.Write("\nCalendar:      ");
                            if (debug)
                                Console.Write("\n Data ==> calendar\n");
//                            fprintf(data, "\n===================== > Calendar < =============\n");
                            Record = 0;
                            break;
                        }
                    default:
                        {
//                            fprintf(data, "\n===================== > UNKNOWN? < =============\n");
                            Console.Write("\nSection 0x{0:X2}:    ", DataBuffer[0]);
                            Record = 0;
                            break;
                        }
                }
            }
            else if (MHeader.nbytes == 0x00)
            {
                if (MHeader.address == 0x0100)
                {
                    Console.Write("End Record# {0}\n\n", ++Record);
                }
                else if (MHeader.address == 0xff00)
                {
                    Console.Write("\nDONE!!\n");
                    Stopped = true;
                }
            }
            else
            {
                Console.WriteLine("{0}", Encoding.ASCII.GetString(DataBuffer, 0, MHeader.nbytes));
            }
        }

#if false

        /******* poll the keyboard for a character **********/
        /********* idea borrowed from The Linux Journal article
        of Sept 95 issue 17 */
        void int kbhit(void)
        {

          struct timeval tv;
          fd_set read_fd;

                /*do not wait at all, not even a microsecond */
                tv.tv_sec = 0;
          tv.tv_usec = 0;

        /* must be done first to initilize read_fd */

          FD_ZERO(&read_fd);

        /* makes select() ask if input is ready :
           * 0 is the file descriptor stdin  */
          FD_SET(0, &read_fd);

        /* the first parameter is the number of the 
           * largest file descriptor to check + 1.  */

          if (select(1, &read_fd,
                  NULL,     /* NO writes */
                  NULL,		/* NO exceptions */
	              &tv)
              == -1)
            return 0;			/* An error occured */
        /* read_fd now holds a bitmap of files that are
           * readable. We test the entry for the standard
           * input (file 0). */

          if (FD_ISSET(0, &read_fd))
        /* character pending on stdin */
            return 1;

        /* no charcaters were pending */

          return 0;
        }

        static void
        sig_catch(int signo)
        {
            Console.Write("\nsignal caught %d\n", signo);
            Console.Write("\nsignal caught %d\n", signo);
            /*inform */
            Console.Write("\nstopping contact with casio \n\n");
            /* should also reset the serial port being used by
               casio and close all files */
            terminate();
        }

        int tty_reset(int fd)       /* restore terminal's mode */
        {
            if (fd == Port)
            {
                if (tcsetattr(fd, TCSAFLUSH, &oldterm) < 0)
                    Console.Write("\nFailed to reset serial port\n");
                return (-1);
            }
            else if (fd == STDIN_FILENO)
            {
                if (tcsetattr(fd, TCSAFLUSH, &save_stdin) < 0)
                    Console.Write("\nFailed to reset stdin\n");
                return (-1);
            }
            return (0);
        }

#endif

#if false
    /* borrowed from W. Richard Stevens book
    Advanced Programming in the Unix Environment */

    static int tty_cbreak(int fd)      /* put terminal into a cbreak mode */
        {
            struct termios buf;

            if (fd == STDIN_FILENO)
            {
                if (tcgetattr(fd, &save_stdin) < 0)
	                return (-1);
            }

            buf = save_stdin;		/* structure copy */

            buf.c_lflag &= ~(ECHO | ICANON);
            /* echo off, canonical mode off */

            buf.c_cc[VMIN] = 1;		/* Case B: 1 byte at a time, no timer */
            buf.c_cc[VTIME] = 0;

            if (tcsetattr(fd, TCSAFLUSH, &buf) < 0)
                return (-1);
            return (0);
        }
#endif

        static void Terminate()
        {
#if false
            blkflg |= O_NONBLOCK;
            if (fcntl(Port, F_SETFL, blkflg) < 0)
            {
                fprintf(stderr, "\nexiting ..\n");
                exit(-1);
            }
#endif
            if (debug)
                Console.Write("\nterminate: writting stop to port\n");
            Stopped = true;
            WriteByte(STOP);

            /*reset terminals */
            if (debug)
                Console.Write("\n reseting stdin\n");
//            tty_reset(STDIN_FILENO);
            if (debug)
                Console.Write("\n reseting port\n");
//            tty_reset(Port);

            /*close all open files */
            if (debug)
                Console.Write("\n closing casiofile\n");
//            fclose(casiofile);
            if (debug)
                Console.Write("\n closing Port\n");
            port.Close();
            if (debug)
                Console.Write("\n closing data file\n");
//            fclose(data);
            if (debug)
                Console.Write("\n closing debug file\n");
//            fclose(dbg);
//            exit(0);
            throw new Exception();
        }

        /*
         * R e a d B y t e
         *
         *  Read a byte from the COM port
         */

        public static int ReadByte(Mode mode)
        {
            int i;

            long TimeOut;
            if (LastRead != 0)
            {
                if (debug2)
                    Console.Write("we did read {0} before ==> just return \n", (char)LastRead);
                i = LastRead;
                LastRead = 0;
                return (i);
            }

            while (true)
            {
                for (TimeOut = 0; TimeOut < TIMEOUT; TimeOut++)
                {
                    if (Program.Stopped)
                    {
                        return (-1);
                    }

                    /*
                     * Check if the user pressed ESC to stop communication
                     */
#if false
                    if (kbhit())
                    {
                        if (getchar() == 0x1b)
                        {
                            Console.Write("\n\nESC pressed, stop communication\n");
                            Stopped = true;
                            WriteByte(STOP);
                            return (-1);
                        }
                    }
#endif
                    if (debug2)
                        Console.Write("\n trying to read\n ");

                    /* read a char and store in i */
                    if (mode == Mode.NOWAIT && port.BytesToRead == 0)
                        return -1;

                    i = port.ReadByte();

                    if (debug2)
                        Console.Write("\n read\n ");
                    if (i == -1)
                    {
                        if (debug2)
                            Console.Write("\nEOF encountered\n");
                    }
                    if (i > 0)
                    {
                        if (debug2)
                            Console.Write("\nsuccessful READ\n");
                    }
#if false
                    if (j == -1)
                    {
                        if (debug2)
                            Console.Write("\nUnsuccessfull READ\n");
                    }
#endif
                    if (debug2)
                        Console.Write("Received byte {0:X2}\n", i);

                    i &= 0xff;
                    if (i == STOP)
                    {
                        Console.Write("\nCASIO has stopped communication\n");
                        Console.Write("\nSTOP received \n");
                        Program.Stopped = true;
                    }

                    if (i == XOFF)
                    {
                        WriteStatus = XOFF;
                    }

                    if (i == XON)
                    {
                        WriteStatus = XON;
                    }
                    return (i);
                }
                Console.Write("\n timeout\n");


                if (mode == Mode.NOWAIT)
                {
                    return (-1);
                }
            }
        }

        /*
         * W r i t e B y t e
         *
         *  Write a byte to the COM port
         */

        static void WriteByte(char d)
        {
            int i;
            long TimeOut;

            if (debug2)
                Console.Write("\nWRITEBYTE: writestatus = {0:x}\n", WriteStatus);
            if (!NOCHECK && !Stopped)
            {

                if ((i = ReadByte(Mode.NOWAIT)) == XOFF)
                {
                    WriteStatus = XOFF;
                }
                else
                {
                    LastRead = i;
                }

                if (WriteStatus == XOFF)
                {
                    while (ReadByte(Mode.WAIT) != XON && !Stopped) ;

                    WriteStatus = XON;
                }
            }

            TimeOut = 0;
            if (debug)
                Console.Write("\n sending {0:X2} to serial port \n", (int)d);

            /* write the character to the serial port */
            try
            {
                port.Write(new char[] { d }, 0, 1);
            }
            catch(Exception)
            {
                Console.Write("\nERROR WriteByte: couldnt write the char {0:X2} to serial port \n", (int)d);
                Terminate();
            }
            if (Direction == WRITE)
            {
                System.Threading.Thread.Sleep(4);
                // usleep(4000);
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("CasioSharp.exe -[r|w|p] <file>");
            Console.WriteLine("   CasioSharp.exe -r <outFile>  -- backup Casio to outFile");
            Console.WriteLine("   CasioSharp.exe -w <inFile>  -- write inFile to Casio");
            Console.WriteLine("   CasioSharp.exe -p <inFile>  -- print inFile\n");
            var names = SerialPort.GetPortNames();
            foreach (var item in names)
            {
                Console.WriteLine("Serial port: {0}", item);
            }


        }
    }
}
