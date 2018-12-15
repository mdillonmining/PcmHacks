﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcmHacking
{
    /// <summary>
    /// How much of the PCM to erase and rewrite.
    /// </summary>
    public enum WriteType
    {
        Invalid = 0,
        Calibration = 1,
        OsAndCalibration = 2,
        Full = 3,
    }

    public partial class Vehicle
    {
        /// <summary>
        /// Replace the full contents of the PCM.
        /// </summary>
        public async Task<bool> Write(WriteType writeType, bool kernelRunning, bool recoveryMode, CancellationToken cancellationToken, Stream stream)
        {
            byte[] image = new byte[stream.Length];
            int bytesRead = await stream.ReadAsync(image, 0, (int)stream.Length);
            if (bytesRead != stream.Length)
            {
                this.logger.AddUserMessage("Unable to read input file.");
                return false;
            }

            try
            {
                this.device.ClearMessageQueue();

                if (!kernelRunning)
                {
                    // switch to 4x, if possible. But continue either way.
                    // if the vehicle bus switches but the device does not, the bus will need to time out to revert back to 1x, and the next steps will fail.
//                    if (!await this.VehicleSetVPW4x(VpwSpeed.FourX))
//                    {
//                        this.logger.AddUserMessage("Stopping here because we were unable to switch to 4X.");
//                        return false;
//                    }

                    Response<byte[]> response = await LoadKernelFromFile("write-kernel.bin");
                    if (response.Status != ResponseStatus.Success)
                    {
                        logger.AddUserMessage("Failed to load kernel from file.");
                        return false;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    // TODO: instead of this hard-coded address, get the base address from the PcmInfo object.
                    if (!await PCMExecute(response.Value, 0xFF8000, cancellationToken))
                    {
                        logger.AddUserMessage("Failed to upload kernel to PCM");

                        return false;
                    }

//                    await toolPresentNotifier.Notify();

                    logger.AddUserMessage("Kernel uploaded to PCM succesfully.");
                }

                await this.device.SetTimeout(TimeoutScenario.Maximum);

                bool success;
                switch (writeType)
                {
                    case WriteType.Calibration:
                        success = await this.CalibrationWrite(cancellationToken, stream);
                        break;

                    case WriteType.OsAndCalibration:
                        success = await this.OsAndCalibrationWrite(cancellationToken, stream);
                        break;

                    case WriteType.Full:
                        success = await this.FullWrite(cancellationToken, stream);
                        await TryWriteJsKernelReset(cancellationToken);
                        break;
                }

                await this.Cleanup();
                return true;
            }
            catch (Exception exception)
            {
                this.logger.AddUserMessage("Something went wrong. " + exception.Message);
                this.logger.AddUserMessage("Do not power off the PCM! Do not exit this program!");
                this.logger.AddUserMessage("Try flashing again. If errors continue, seek help online.");
                this.logger.AddUserMessage("https://pcmhacking.net/forums/viewtopic.php?f=3&t=6080");
                this.logger.AddDebugMessage(exception.ToString());
                return false;
            }
        }

        public async Task<bool> IsKernelRunning()
        {
            Message query = this.messageFactory.CreateFlashMemoryTypeQuery();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!await this.device.SendMessage(query))
                {
                    await Task.Delay(250);
                    continue;
                }

                Message reply = await this.device.ReceiveMessage();
                if (reply == null)
                {
                    await Task.Delay(250);
                    continue;
                }

                Response<UInt32> response = this.messageParser.ParseFlashMemoryType(reply);
                if (response.Status == ResponseStatus.Success)
                {
                    return true;
                }

                if (response.Status == ResponseStatus.Refused)
                {
                    return false;
                }

                await Task.Delay(250);
            }

            return false;
        }

        private async Task<bool> CalibrationWrite(CancellationToken cancellationToken, Stream stream)
        {
            // Which flash chip?
            Query<UInt32> chipIdQuery = new Query<uint>(
                this.device,
                this.messageFactory.CreateFlashMemoryTypeQuery,
                this.messageParser.ParseFlashMemoryType,
                this.logger);
            Response<UInt32> chipIdResponse = await chipIdQuery.Execute();

            if (chipIdResponse.Status != ResponseStatus.Success)
            {
                logger.AddUserMessage("Unable to determine which flash chip is in this PCM");
                return false;
            }

            FlashMemoryType memoryType;
            switch (chipIdResponse.Value)
            {
                case 0x12341234:
                    memoryType = FlashMemoryType.Intel512;
                    break;

                default:
                    this.logger.AddUserMessage("Unsupported flash chip ID " + chipIdResponse.Value + ". " +
                        Environment.NewLine +
                        "The flash memory in this PCM is not supported by this version of PCM Hammer." +
                        Environment.NewLine +
                        "Please look for a thread about this at pcmhacking.net, or create one if necessary." +
                        Environment.NewLine +
                        "We aim to add support for all flash chips over time.");
                    return false;
            }

            // Get CRC ranges
            IList<MemoryRange> ranges = this.GetMemoryRanges(memoryType);
            if (ranges == null)
            {
                this.logger.AddUserMessage("Unsupported flash memory format " + memoryType + ". " +
                    Environment.NewLine +
                    "The flash memory in this PCM is not supported by this version of PCM Hammer." +
                    Environment.NewLine +
                    "Please look for a thread about this at pcmhacking.net, or create one if necessary." +
                    Environment.NewLine +
                    "We aim to add support for all flash chips over time.");
                return false;
            }

            foreach (MemoryRange range in ranges)
            {
                Query<UInt32> crcQuery = new Query<uint>(
                    this.device,
                    () => this.messageFactory.CreateCrcQuery(range.Address, range.Size),
                    this.messageParser.ParseCrc,
                    this.logger);
                Response<UInt32> crcResponse = await chipIdQuery.Execute();

                if (crcResponse.Status != ResponseStatus.Success)
                {
                    this.logger.AddUserMessage("Unable to get CRC for memory range " + range.Address.ToString("X8") + " / " + range.Size.ToString("X8"));
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> OsAndCalibrationWrite(CancellationToken cancellationToken, Stream stream)
        {
            await Task.Delay(0);
            return true;
        }

        private async Task<bool> FullWrite(CancellationToken cancellationToken, Stream stream)
        {
            Message start = new Message(new byte[] { 0x6C, 0x10, 0xF0, 0x3C, 0x01 });

            if (!await this.JS_SendMessageValidateResponse(
                start,
                this.messageParser.ParseStartFullFlashResponse,
                "start full flash",
                "Full flash starting.",
                "Kernel won't allow a full flash.",
                cancellationToken))
            {
                return false;
            }
            
            byte chunkSize = 192;
            byte[] header = new byte[] { 0x6C, 0x10, 0x0F0, 0x3C, 0x00, 0x00, chunkSize, 0xFF, 0xA0, 0x00 };
            byte[] messageBytes = new byte[header.Length + chunkSize + 2];
            Buffer.BlockCopy(header, 0, messageBytes, 0, header.Length);
            for (int bytesSent = 0; bytesSent < stream.Length; bytesSent += chunkSize)
            {
                stream.Read(messageBytes, header.Length, chunkSize);
                VPWUtils.AddBlockChecksum(messageBytes); // TODO: Move this function into the Message class.
                Message message = new Message(messageBytes);

                if (!await this.JS_SendMessageValidateResponse(
                    message,
                    this.messageParser.ParseChunkWriteResponse,
                    string.Format("data from {0} to {1}", bytesSent, bytesSent + chunkSize),
                    "Data chunk sent.",
                    "Unable to send data chunk.",
                    cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> TryWaitForJsKernel(CancellationToken cancellationToken, int maxAttempts)
        {
            logger.AddUserMessage("Waiting for kernel to respond.");

            return await this.JS_SendMessageValidateResponse(
                this.messageFactory.CreateJsKernelPing(),
                this.messageParser.ParseKernelPingResponse,
                "kernel ping",
                "Kernel is responding.",
                "No response received from the flash kernel.",
                cancellationToken,
                maxAttempts,
                false);
        }

        private async Task<bool> TryWriteJsKernelReset(CancellationToken cancellationToken)
        {
            return await this.JS_SendMessageValidateResponse(
                this.messageFactory.CreateJsWriteKernelResetRequest(),
                this.messageParser.ParseWriteKernelResetResponse,
                "flash-kernel PCM reset request",
                "PCM reset.",
                "Unable to reset the PCM.",
                cancellationToken);
        }

        public IList<MemoryRange> GetMemoryRanges(FlashMemoryType flashMemoryType)
        {
            switch (flashMemoryType)
            {
                case FlashMemoryType.Intel512:
                    return new MemoryRange[]
                    {
                        new MemoryRange(0,0),
                    };

                default:
                    return null;
            }
        }

        private async Task<bool> JS_SendMessageValidateResponse(
            Message message,
            Func<Message, Response<bool>> filter,
            string messageDescription,
            string successMessage,
            string failureMessage,
            CancellationToken cancellationToken,
            int maxAttempts = 5,
            bool pingKernel = false)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                this.logger.AddUserMessage("Sending " + messageDescription);

                if (!await this.TrySendMessage(message, messageDescription, maxAttempts))
                {
                    this.logger.AddUserMessage("Unable to send " + messageDescription);
                    if (pingKernel)
                    {
                        await this.TryWaitForJsKernel(cancellationToken, 1);
                    }
                    continue;
                }

                if (!await this.WaitForSuccess(filter, cancellationToken, 10))
                {
                    this.logger.AddUserMessage("No " + messageDescription + " response received.");
                    if (pingKernel)
                    {
                        await this.TryWaitForJsKernel(cancellationToken, 1);
                    }
                    continue;
                }

                this.logger.AddUserMessage(successMessage);
                return true;
            }

            this.logger.AddUserMessage(failureMessage);
            if (pingKernel)
            {
                await this.TryWaitForJsKernel(cancellationToken, 1);
            }
            return false;
        }
    }
}
