﻿using BioLib;
using BioLib.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace godotdec {
	class Program {
		private const string VERSION = "2.1.2";
		private const string PROMPT_ID = "godotdec_overwrite";
		private const int MAGIC_PACKAGE = 0x43504447;
		private const int MAGIC_RSRC = 0x43525352;
		private const int TEXTURE_V1_FORMAT_BIT_PNG = 1 << 20;
		private const int TEXTURE_V1_FORMAT_BIT_WEBP = 1 << 21;
		private const int TEXTURE_V2_FORMAT_BIT_PNG = 1;
		private const int TEXTURE_V2_FORMAT_BIT_WEBP = 2;

		private static string inputFile;
		private static string outputDirectory;
		private static bool convertAssets;

		static void Main(string[] args) {
			Bio.Header("godotdec", VERSION, "2018-2020", "A simple unpacker for Godot Engine package files (.pck|.exe)",
				"[<options>] <input_file> [<output_directory>]\n\nOptions:\n-c\t--convert\tConvert textures and audio files");

			if (Bio.HasCommandlineSwitchHelp(args)) return;
			ParseCommandLine(args.ToList());

			var failed = 0;
			using (var inputStream = new BinaryReader(File.Open(inputFile, FileMode.Open))) {
				if (inputStream.ReadInt32() != MAGIC_PACKAGE) {
					inputStream.BaseStream.Seek(-4, SeekOrigin.End);

					CheckMagic(inputStream.ReadInt32());

					inputStream.BaseStream.Skip(-12);
					var offset = inputStream.ReadInt64();
					inputStream.BaseStream.Skip(-offset - 8);

					CheckMagic(inputStream.ReadInt32());
				}
				
				int packFormatVersion = inputStream.ReadInt32();
				Bio.Cout($"Package format version: {packFormatVersion}");
				Bio.Cout($"Godot Engine version: {inputStream.ReadInt32()}.{inputStream.ReadInt32()}.{inputStream.ReadInt32()}");

				if (packFormatVersion <= 1) {
					// No special handling
				}
				else if (packFormatVersion == 2) {
					uint packFlags = inputStream.ReadUInt32();
					if ((packFlags & 1) != 0) // PACK_DIR_ENCRYPTED
						Bio.Error("Encrypted directory not supported.", Bio.EXITCODE.NOT_SUPPORTED);
				}
				else {
					Bio.Error($"Package format version {packFormatVersion} is not supported.", Bio.EXITCODE.NOT_SUPPORTED);
				}

				long filesBaseOffset = 0;
				if (packFormatVersion >= 2) filesBaseOffset = inputStream.ReadInt64();

				// Skip reserved bytes (16x Int32)
				inputStream.BaseStream.Skip(16 * 4);

				var fileCount = inputStream.ReadInt32();
				Bio.Cout($"Found {fileCount} files in package");
				Bio.Cout("Reading file index");

				var fileIndex = new List<FileEntry>();
				for (var i = 0; i < fileCount; i++) {
					var pathLength = inputStream.ReadInt32();
					var path = Encoding.UTF8.GetString(inputStream.ReadBytes(pathLength));
					var fileEntry = new FileEntry(path.ToString(), inputStream.ReadInt64() + filesBaseOffset, inputStream.ReadInt64());
					fileIndex.Add(fileEntry);
					//Bio.Debug(fileEntry);
					inputStream.BaseStream.Skip(16);

					if (packFormatVersion >= 2) {
						var fileFlags = inputStream.ReadUInt32();
						if ((fileFlags & 1) != 0) Bio.Error("Encrypted files not supported.", Bio.EXITCODE.NOT_SUPPORTED);
					}
				}

				if (fileIndex.Count < 1) Bio.Error("No files were found inside the archive", Bio.EXITCODE.RUNTIME_ERROR);
				fileIndex.Sort((a, b) => (int) (a.offset - b.offset));

				Bio.Cout("Extracting files...");
				if (convertAssets) Bio.Cout("File conversion is enabled");

				var fileIndexEnd = inputStream.BaseStream.Position;
				for (var i = 0; i < fileIndex.Count; i++) {
					var fileEntry = fileIndex[i];
					Bio.Progress(fileEntry.path, i+1, fileIndex.Count);

					if (fileEntry.offset < fileIndexEnd) {
						Bio.Warn("Invalid file offset: " + fileEntry.offset);
						continue;
					}

					if (ConvertFile(inputStream, fileEntry)) continue;

					inputStream.BaseStream.Position = fileEntry.offset;
					var destination = GetOutputPath(fileEntry);

					try {
						Action<Stream, Stream> copyFunction = (input, output) => input.Copy(output, (int) fileEntry.size);
						inputStream.BaseStream.WriteToFileRelative(destination, PROMPT_ID, copyFunction);
					}
					catch (Exception e) {
						Bio.Error(e);
						failed++;
					}
				}
			}

			Bio.Cout();
			Bio.Cout(failed < 1? "All OK": failed + " files failed to extract");
			Bio.Pause();
		}

		static string GetOutputPath(FileEntry fileEntry) {
			return Path.Combine(outputDirectory, fileEntry.path);
		}

		static void ParseCommandLine(List<string> args) {
			convertAssets = args.Remove("--convert") || args.Remove("-c");
			if (args.Count < 1) Bio.Error("Please specify the path to a Godot .pck or .exe file.", Bio.EXITCODE.INVALID_INPUT);

			inputFile = args[0];
			if (!File.Exists(inputFile)) Bio.Error("The input file " + inputFile + " does not exist. Please make sure the path is correct.", Bio.EXITCODE.IO_ERROR);
			
			outputDirectory = args.Count > 1? args[1]: Path.Combine(Path.GetDirectoryName(inputFile), Path.GetFileNameWithoutExtension(inputFile));

			Bio.Debug("Input file: " + inputFile);
			Bio.Debug("Output directory: " + outputDirectory);
		}

		static void CheckMagic(int magic) {
			if (magic == MAGIC_PACKAGE) return;
			Bio.Error("The input file is not a valid Godot package file.", Bio.EXITCODE.INVALID_INPUT);
		}

		static bool ConvertFile(BinaryReader binaryReader, FileEntry fileEntry) {
			if (!convertAssets) return false;

			var internalPath = fileEntry.path.ToLower();
			binaryReader.BaseStream.Position = fileEntry.offset;

			// https://github.com/godotengine/godot/blob/3.5/scene/resources/texture.cpp#L464
			if (internalPath.EndsWith(".stex")) {
				binaryReader.BaseStream.Skip(12);
				var format = binaryReader.ReadInt32();

				if ((format & TEXTURE_V1_FORMAT_BIT_PNG) > 0) {
					fileEntry.ChangeExtension(".stex", ".png");
				}
				else if ((format & TEXTURE_V1_FORMAT_BIT_WEBP) > 0) {
					fileEntry.ChangeExtension(".stex", ".webp");
				}
				else {
					Bio.Debug("Unknown texture format");
				}

				fileEntry.Resize(32);
			}
			// https://github.com/godotengine/godot/blob/4.2/scene/resources/compressed_texture.cpp#L299
			else if (internalPath.EndsWith(".ctex")) {
				binaryReader.BaseStream.Skip(36);
				var format = binaryReader.ReadInt32();
				binaryReader.BaseStream.Skip(16);

				if (format == TEXTURE_V2_FORMAT_BIT_PNG) {
					fileEntry.ChangeExtension(".ctex", ".png");
				}
				else if (format == TEXTURE_V2_FORMAT_BIT_WEBP) {
					fileEntry.ChangeExtension(".ctex", ".webp");
				}
				else {
					Bio.Debug("Unknown texture format");
				}

				fileEntry.Resize(56);
			}
			// https://github.com/godotengine/godot/blob/master/core/io/resource_format_binary.cpp#L836
			else if (internalPath.EndsWith(".oggstr")) {
				fileEntry.Resize(279, 4);
				fileEntry.ChangeExtension(".oggstr", ".ogg");
			}
			// https://github.com/godotengine/godot/blob/3.5/scene/resources/audio_stream_sample.cpp#L552
			else if (internalPath.EndsWith(".sample")) {
				var properties = ParseResource(binaryReader, fileEntry);
				if (properties == null) return false;
				return ExtractWav(properties, fileEntry);
			}

			return false;
		}

		/// <summary>
		/// Parses RSRC files and returns the offset of the first found internal resource
		/// </summary>
		static SerializedObject ParseResource(BinaryReader binaryReader, FileEntry fileEntry) {
			var magic = binaryReader.ReadInt32();
			if (magic != MAGIC_RSRC) {
				Bio.Warn("Invalid resource header, cannot convert file.");
				Bio.Debug(binaryReader.BaseStream);
				return null;
			}

			var bigEndian = binaryReader.ReadInt32();
			if (bigEndian > 0) Bio.Warn("Big endian resources are currently not supported. Extracted file might not be readable.");

			binaryReader.BaseStream.Skip(4); // useReal64
			var versionMajor = binaryReader.ReadInt32();
			var versionMinor = binaryReader.ReadInt32();
			var versionPatch = binaryReader.ReadInt32();

			var resourceType = binaryReader.Read32BitPrefixedString(true, true);

			Bio.Debug($"{resourceType} resource, version {versionMajor}.{versionMinor}.{versionPatch}");
			binaryReader.BaseStream.Skip(14 * 4 + 8); // importmd_ofs + reserved fields

			var stringTableSize = binaryReader.ReadInt32();
			var stringTable = new string[stringTableSize];
			for (var i = 0; i < stringTableSize; i++) {
				stringTable[i] = binaryReader.Read32BitPrefixedString(true, true);
			}

			var externalResourceCount = binaryReader.ReadInt32();
			for (var i = 0; i < externalResourceCount; i++) {
				var type = binaryReader.Read32BitPrefixedString(true, true);
				var path = binaryReader.Read32BitPrefixedString(true, true);
				Bio.Debug($"External resource: {type} {path}");
			}

			var internalResourceCount = binaryReader.ReadInt32();
			var internalResourceOffsets = new long[internalResourceCount];
			for (var i = 0; i < internalResourceCount; i++) {
				var path = binaryReader.Read32BitPrefixedString(true, true);
				internalResourceOffsets[i] = binaryReader.ReadInt64();
				Bio.Debug($"Internal resource: {path} @ {internalResourceOffsets[i]}");
			}

			if (internalResourceOffsets.Length < 1) {
				Bio.Warn("No internal resources found in RSRC file. Conversion not possible.");
				return null;
			}

			binaryReader.BaseStream.Position = fileEntry.offset + internalResourceOffsets[0];
			var name = binaryReader.Read32BitPrefixedString(true, true);
			var propertyCount = binaryReader.ReadInt32();
			Bio.Debug($"Resource type { name} with {propertyCount} properties");

			var properties = new Dictionary<string, dynamic>();
			for (var i = 0; i < propertyCount; i++) {
				var nameIndex = binaryReader.ReadInt32();
				var propertyType = (Variant) binaryReader.ReadInt32();

				dynamic value = null;
				switch (propertyType) {
					case Variant.NIL:
						break;
					case Variant.VARIANT_RAW_ARRAY:
						var size = binaryReader.ReadInt32();
						value = binaryReader.BaseStream.Extract(size);
						break;
					case Variant.INT:
						value = binaryReader.ReadInt32();
						break;
				}

				properties.Add(stringTable[nameIndex], value);
			}

			Bio.Debug(properties);
			return new SerializedObject(name, properties);
		}

		static bool ExtractWav(SerializedObject serializedObject, FileEntry fileEntry) {
			if (serializedObject.name != "AudioStreamWAV") {
				Bio.Warn("Resource is not AudioStreamWAV, conversion not possible");
				return false;
			}

			if (!serializedObject.properties.TryGetValue("data", out dynamic dataRaw)) {
				Bio.Warn("Failed to get audio data, conversion not possible");
				return false;
			}

			serializedObject.properties.TryGetValue("stereo", out dynamic stereo);
			var data = (MemoryStream) dataRaw;
			var subChunk2Size = (int) data.Length;
			var channels = (stereo ?? false)? 2: 1;
			var formatCode = (WavFormat) serializedObject.properties["format"];
			var sampleRate = (int) serializedObject.properties["mix_rate"];
			var bytesPerSample = formatCode == WavFormat.FORMAT_8_BITS? 1: formatCode == WavFormat.FORMAT_16_BITS? 2: 4;

			fileEntry.ChangeExtension(".sample", ".wav");
			var memoryStream = new MemoryStream(44);
			var binaryWriter = new BinaryWriter(memoryStream);

			binaryWriter.Write(Encoding.ASCII.GetBytes("RIFF"));
			binaryWriter.Write(BitConverter.GetBytes(subChunk2Size + 36));
			binaryWriter.Write(Encoding.ASCII.GetBytes("WAVE"));
			binaryWriter.Write(Encoding.ASCII.GetBytes("fmt "));
			binaryWriter.Write(BitConverter.GetBytes(16));
			binaryWriter.Write(BitConverter.GetBytes((short) formatCode));
			binaryWriter.Write(BitConverter.GetBytes((short) channels));
			binaryWriter.Write(BitConverter.GetBytes(sampleRate));
			binaryWriter.Write(BitConverter.GetBytes(sampleRate * channels * bytesPerSample));
			binaryWriter.Write(BitConverter.GetBytes((short) (channels * bytesPerSample)));
			binaryWriter.Write(BitConverter.GetBytes((short) (bytesPerSample * 8)));
			binaryWriter.Write(Encoding.ASCII.GetBytes("data"));
			binaryWriter.Write(BitConverter.GetBytes(subChunk2Size));

			var outputStream = binaryWriter.BaseStream.Concatenate(data);
			var destination = GetOutputPath(fileEntry);
			outputStream.WriteToFile(destination, PROMPT_ID);

			return true;
		}
	}
}

class FileEntry {
	public string path;
	public long offset;
	public long size;

	public FileEntry (string path, long offset, long size) {
		this.path = path.Substring(6).TrimEnd('\0');
		this.offset = offset;
		this.size = size;
	}

	public void Resize(int by, int stripAtEnd = 0) {
		offset += by;
		size -= by + stripAtEnd;
	}

	public void ChangeExtension(string from, string to) {
		path = path.Replace(from, to);
	}

	public override string ToString() {
		return $"{offset:000000} {path}, {size}";
	}
}

class SerializedObject {
	public string name;
	public Dictionary<string, dynamic> properties;

	public SerializedObject(string name, Dictionary<string, dynamic> properties) {
		this.name = name;
		this.properties = properties;
	}
}

enum Variant {
	NIL = 1,
	BOOL = 2,
	INT = 3,
	VARIANT_RAW_ARRAY = 31
}

enum WavFormat {
	FORMAT_8_BITS,
	FORMAT_16_BITS,
	FORMAT_IMA_ADPCM
};