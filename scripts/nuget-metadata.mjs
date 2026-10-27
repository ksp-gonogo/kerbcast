/**
 * Reads the parts of a .NET assembly's PE image the NuGet release scripts need:
 * the assemblies it references, its own AssemblyVersion, and the byte regions a
 * rebuild of identical source changes anyway.
 *
 * Every offset is computed from the file's own headers and table sizes, never
 * assumed, so a different compiler emitting a different layout still reads
 * correctly. Layouts follow ECMA-335 partition II, sections 22 to 24.
 */

/** Table numbers this reader names; every other present table is only sized. */
const T = {
  Module: 0x00,
  TypeRef: 0x01,
  TypeDef: 0x02,
  Field: 0x04,
  MethodDef: 0x06,
  Param: 0x08,
  InterfaceImpl: 0x09,
  MemberRef: 0x0a,
  DeclSecurity: 0x0e,
  StandAloneSig: 0x11,
  Event: 0x14,
  Property: 0x17,
  ModuleRef: 0x1a,
  TypeSpec: 0x1b,
  Assembly: 0x20,
  AssemblyRef: 0x23,
  File: 0x26,
  ExportedType: 0x27,
  ManifestResource: 0x28,
  GenericParam: 0x2a,
  MethodSpec: 0x2b,
  GenericParamConstraint: 0x2c,
};

const CODED = {
  TypeDefOrRef: [T.TypeDef, T.TypeRef, T.TypeSpec],
  HasConstant: [T.Field, T.Param, T.Property],
  HasCustomAttribute: [
    T.MethodDef, T.Field, T.TypeRef, T.TypeDef, T.Param, T.InterfaceImpl,
    T.MemberRef, T.Module, T.DeclSecurity, T.Property, T.Event,
    T.StandAloneSig, T.ModuleRef, T.TypeSpec, T.Assembly, T.AssemblyRef,
    T.File, T.ExportedType, T.ManifestResource, T.GenericParam,
    T.GenericParamConstraint, T.MethodSpec,
  ],
  HasFieldMarshal: [T.Field, T.Param],
  HasDeclSecurity: [T.TypeDef, T.MethodDef, T.Assembly],
  MemberRefParent: [T.TypeDef, T.TypeRef, T.ModuleRef, T.MethodDef, T.TypeSpec],
  HasSemantics: [T.Event, T.Property],
  MethodDefOrRef: [T.MethodDef, T.MemberRef],
  MemberForwarded: [T.Field, T.MethodDef],
  ResolutionScope: [T.Module, T.ModuleRef, T.AssemblyRef, T.TypeRef],
  /* Two of its five tags are unused, but the tag still takes three bits. */
  CustomAttributeType: [T.MethodDef, T.MemberRef, -1, -1, -1],
};

function parsePe(buf) {
  if (buf.length < 0x40 || buf.readUInt16LE(0) !== 0x5a4d) {
    throw new Error("not a PE file: missing 'MZ' at offset 0");
  }
  const peOffset = buf.readUInt32LE(0x3c);
  if (buf.readUInt32LE(peOffset) !== 0x00004550) {
    throw new Error(`not a PE file: missing 'PE\\0\\0' at 0x${peOffset.toString(16)}`);
  }
  const coff = peOffset + 4;
  const numberOfSections = buf.readUInt16LE(coff + 2);
  const sizeOfOptionalHeader = buf.readUInt16LE(coff + 16);
  const opt = coff + 20;
  /* PE32+ widens the fields above the data directories, which moves them. */
  const plus = buf.readUInt16LE(opt) === 0x20b;
  const numberOfRvaAndSizes = buf.readUInt32LE(opt + (plus ? 108 : 92));
  const dataDirs = opt + (plus ? 112 : 96);

  const sections = [];
  const table = opt + sizeOfOptionalHeader;
  for (let i = 0; i < numberOfSections; i++) {
    const s = table + i * 40;
    sections.push({
      va: buf.readUInt32LE(s + 12),
      vsize: buf.readUInt32LE(s + 8),
      rawSize: buf.readUInt32LE(s + 16),
      raw: buf.readUInt32LE(s + 20),
    });
  }
  const fileOffset = (rva) => {
    for (const s of sections) {
      if (rva >= s.va && rva < s.va + Math.max(s.vsize, s.rawSize)) return s.raw + (rva - s.va);
    }
    throw new Error(`RVA 0x${rva.toString(16)} is not inside any section`);
  };
  const dataDirectory = (i) =>
    i >= numberOfRvaAndSizes
      ? { rva: 0, size: 0 }
      : { rva: buf.readUInt32LE(dataDirs + i * 8), size: buf.readUInt32LE(dataDirs + i * 8 + 4) };

  return { coff, fileOffset, dataDirectory };
}

function parseMetadata(buf, pe) {
  const clr = pe.dataDirectory(14);
  if (clr.size === 0) throw new Error("no CLI header: not a .NET assembly");
  const root = pe.fileOffset(buf.readUInt32LE(pe.fileOffset(clr.rva) + 8));
  if (buf.readUInt32LE(root) !== 0x424a5342) throw new Error("no BSJB metadata root");
  let p = root + 16 + Math.ceil(buf.readUInt32LE(root + 12) / 4) * 4 + 2;
  const count = buf.readUInt16LE(p);
  p += 2;
  const streams = {};
  for (let i = 0; i < count; i++) {
    const offset = buf.readUInt32LE(p);
    const size = buf.readUInt32LE(p + 4);
    p += 8;
    let end = p;
    while (buf[end] !== 0) end++;
    streams[buf.toString("ascii", p, end)] = { offset: root + offset, size };
    p += Math.ceil((end - p + 1) / 4) * 4;
  }
  const tables = streams["#~"] ?? streams["#-"];
  if (!tables) throw new Error("no metadata tables stream");

  const base = tables.offset;
  const heapSizes = buf.readUInt8(base + 6);
  const strIdx = heapSizes & 0x01 ? 4 : 2;
  const guidIdx = heapSizes & 0x02 ? 4 : 2;
  const blobIdx = heapSizes & 0x04 ? 4 : 2;
  const valid = buf.readBigUInt64LE(base + 8);

  const rows = new Array(64).fill(0);
  let q = base + 24;
  for (let t = 0; t < 64; t++) {
    if ((valid >> BigInt(t)) & 1n) {
      rows[t] = buf.readUInt32LE(q);
      q += 4;
    }
  }
  /* The uncompressed #- stream may carry four bytes of extra data here. */
  if (heapSizes & 0x40) q += 4;

  const idx = (t) => (rows[t] < 0x10000 ? 2 : 4);
  const coded = (tables) => {
    const bits = Math.ceil(Math.log2(tables.length));
    const max = Math.max(...tables.map((t) => (t < 0 ? 0 : rows[t])));
    return max < 1 << (16 - bits) ? 2 : 4;
  };
  const c = Object.fromEntries(Object.entries(CODED).map(([k, v]) => [k, coded(v)]));

  /* Row widths of every table up to AssemblyRef, in table-number order. */
  const widths = [
    2 + strIdx + guidIdx * 3, // Module
    c.ResolutionScope + strIdx * 2, // TypeRef
    4 + strIdx * 2 + c.TypeDefOrRef + idx(T.Field) + idx(T.MethodDef), // TypeDef
    idx(T.Field), // FieldPtr
    2 + strIdx + blobIdx, // Field
    idx(T.MethodDef), // MethodPtr
    8 + strIdx + blobIdx + idx(T.Param), // MethodDef
    idx(T.Param), // ParamPtr
    4 + strIdx, // Param
    idx(T.TypeDef) + c.TypeDefOrRef, // InterfaceImpl
    c.MemberRefParent + strIdx + blobIdx, // MemberRef
    2 + c.HasConstant + blobIdx, // Constant
    c.HasCustomAttribute + c.CustomAttributeType + blobIdx, // CustomAttribute
    c.HasFieldMarshal + blobIdx, // FieldMarshal
    2 + c.HasDeclSecurity + blobIdx, // DeclSecurity
    6 + idx(T.TypeDef), // ClassLayout
    4 + idx(T.Field), // FieldLayout
    blobIdx, // StandAloneSig
    idx(T.TypeDef) + idx(T.Event), // EventMap
    idx(T.Event), // EventPtr
    2 + strIdx + c.TypeDefOrRef, // Event
    idx(T.TypeDef) + idx(T.Property), // PropertyMap
    idx(T.Property), // PropertyPtr
    2 + strIdx + blobIdx, // Property
    2 + idx(T.MethodDef) + c.HasSemantics, // MethodSemantics
    idx(T.TypeDef) + c.MethodDefOrRef * 2, // MethodImpl
    strIdx, // ModuleRef
    blobIdx, // TypeSpec
    2 + c.MemberForwarded + strIdx + idx(T.ModuleRef), // ImplMap
    4 + idx(T.Field), // FieldRVA
    8, // EncLog
    4, // EncMap
    16 + blobIdx + strIdx * 2, // Assembly
    4, // AssemblyProcessor
    12, // AssemblyOS
  ];
  const tableStart = [];
  for (let t = 0; t <= T.AssemblyRef; t++) {
    tableStart[t] = q;
    if (t < widths.length) q += widths[t] * rows[t];
  }

  const strings = streams["#Strings"];
  const readIndex = (at, size) => (size === 4 ? buf.readUInt32LE(at) : buf.readUInt16LE(at));
  const string = (at) => {
    const start = strings.offset + readIndex(at, strIdx);
    let end = start;
    while (buf[end] !== 0) end++;
    return buf.toString("utf8", start, end);
  };
  const version = (at) =>
    [0, 2, 4, 6].map((o) => buf.readUInt16LE(at + o)).join(".");

  const assemblyRefs = [];
  for (let r = 0; r < rows[T.AssemblyRef]; r++) {
    const row = tableStart[T.AssemblyRef] + r * (12 + blobIdx * 2 + strIdx * 2);
    assemblyRefs.push({ name: string(row + 12 + blobIdx), version: version(row) });
  }
  const assemblyRow = tableStart[T.Assembly];
  const assembly =
    rows[T.Assembly] === 1
      ? { name: string(assemblyRow + 16 + blobIdx), version: version(assemblyRow + 4) }
      : null;

  /* The Module row's Mvid is an index into #GUID; zero means none recorded. */
  let mvid = null;
  const guid = streams["#GUID"];
  const mvidIndex = readIndex(tableStart[T.Module] + 2 + strIdx, guidIdx);
  if (guid && mvidIndex > 0 && guid.offset + mvidIndex * 16 <= guid.offset + guid.size) {
    mvid = { offset: guid.offset + (mvidIndex - 1) * 16, length: 16, label: "module MVID" };
  }

  return { assembly, assemblyRefs, mvid };
}

/** The assembly's own name and version, and every assembly it references. */
export function readAssembly(buf) {
  const { assembly, assemblyRefs } = parseMetadata(buf, parsePe(buf));
  return { assembly, assemblyRefs };
}

/**
 * Every byte range a rebuild of identical source changes: the PE timestamp,
 * each debug-directory timestamp, the CodeView PDB id, the PDB checksum and the
 * module MVID. SourceLink makes them commit-dependent even under
 * ContinuousIntegrationBuild, so they are zeroed before two builds are compared.
 */
export function identityRegions(buf) {
  const pe = parsePe(buf);
  const regions = [{ offset: pe.coff + 4, length: 4, label: "PE header timestamp" }];

  const debug = pe.dataDirectory(6);
  if (debug.size > 0) {
    const table = pe.fileOffset(debug.rva);
    for (let i = 0; i < Math.floor(debug.size / 28); i++) {
      const e = table + i * 28;
      regions.push({ offset: e + 4, length: 4, label: "debug-directory timestamp" });
      const type = buf.readUInt32LE(e + 12);
      const size = buf.readUInt32LE(e + 16);
      const raw = buf.readUInt32LE(e + 24);
      if (size === 0 || raw === 0) continue;
      if (type === 2 && buf.toString("ascii", raw, raw + 4) === "RSDS") {
        regions.push({ offset: raw + 4, length: 16, label: "CodeView PDB id" });
      } else if (type === 19) {
        /* PdbChecksum: a NUL-terminated algorithm name, then the checksum. */
        let nameEnd = raw;
        while (nameEnd < raw + size && buf[nameEnd] !== 0) nameEnd++;
        if (nameEnd + 1 < raw + size) {
          regions.push({ offset: nameEnd + 1, length: raw + size - nameEnd - 1, label: "PDB checksum" });
        }
      }
    }
  }

  const { mvid } = parseMetadata(buf, pe);
  if (mvid) regions.push(mvid);
  return regions;
}
