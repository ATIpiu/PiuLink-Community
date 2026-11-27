import { http } from '@kit.NetworkKit';
import { fileIo as fs } from '@kit.CoreFileKit';
import fileHash from '@ohos.file.hash';
import { cryptoFramework } from '@kit.CryptoArchitectureKit';
import { util } from '@kit.ArkTS';
import { zlib } from '@kit.BasicServicesKit';

const raw = 'https://raw.githubusercontent.com/ATIpiu/PiuLink-Community/main/updates/';
const assets = 'https://github.com/ATIpiu/PiuLink-Community/releases/download/';
const maxBlock = 256 * 1024;
const hashPattern = /^[a-f0-9]{64}$/;
export interface HapRelease { version: string; name: string; tag: string; bytes: number; sha256: string; mapSha256: string }
interface Block { n: number; h: string }
interface Span { start: number; end: number }
export interface HapResult { path: string; downloaded: number; reused: number }

async function get(url: string, limit: number, range: string = ''): Promise<http.HttpResponse> {
  const client = http.createHttp();
  const headers: Record<string, string> = { 'Accept-Encoding': 'identity' };
  if (range) headers['Range'] = range;
  try {
    return await client.request(url, { expectDataType: http.HttpDataType.ARRAY_BUFFER,
      header: headers,
      usingCache: false, maxLimit: limit, connectTimeout: 20000, readTimeout: 60000 });
  } finally { client.destroy(); }
}

function decode(data: ArrayBuffer): string {
  return new util.TextDecoder('utf-8').decodeToString(new Uint8Array(data));
}

export async function checkRelease(installed: string): Promise<HapRelease | undefined> {
  const res = await get(raw + 'channels.json', 256 * 1024);
  if (res.responseCode !== 200) throw new Error('无法读取 GitHub 更新目录');
  const catalog = JSON.parse(decode(res.result as ArrayBuffer)) as Record<string, HapRelease>;
  const r = catalog['harmony-client'];
  const version = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
  if (!r || !version.test(r.version) || !version.test(installed) ||
      r.name !== 'PiuLink-HarmonyOS-' + r.version + '-unsigned.hap' ||
      !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,150}$/.test(r.tag) ||
      !hashPattern.test(r.sha256) || !hashPattern.test(r.mapSha256) ||
      !Number.isSafeInteger(r.bytes) || r.bytes < 1 || r.bytes > 1024 * 1024 * 1024) {
    throw new Error('鸿蒙更新目录数据有误');
  }
  const a = r.version.split('.');
  const b = installed.split('.');
  for (let i = 0; i < 3; i++) {
    if (Number(a[i]) !== Number(b[i])) return Number(a[i]) > Number(b[i]) ? r : undefined;
  }
  return undefined;
}

async function sha(bytes: Uint8Array): Promise<string> {
  const md = cryptoFramework.createMd('SHA256');
  await md.update({ data: bytes });
  const result = await md.digest();
  let hex = '';
  for (const value of result.data) hex += value.toString(16).padStart(2, '0');
  return hex;
}

async function blocks(r: HapRelease): Promise<Block[]> {
  const response = await get(raw + 'maps/' + r.mapSha256 + '.gz', 2 * 1024 * 1024);
  const packed = response.result as ArrayBuffer;
  if (response.responseCode !== 200 || await sha(new Uint8Array(packed)) !== r.mapSha256) {
    throw new Error('分块索引校验失败');
  }
  const output = new ArrayBuffer(4 * 1024 * 1024);
  const zip = zlib.createZipSync();
  const stream: zlib.ZStream = {};
  await zip.inflateInit2(stream, 31);
  try {
    stream.nextIn = packed;
    stream.availableIn = packed.byteLength;
    stream.nextOut = output;
    stream.availableOut = output.byteLength;
    if (await zip.inflate(stream, zlib.CompressFlushMode.FINISH) !== zlib.ReturnStatus.STREAM_END) {
      throw new Error('分块索引解压失败');
    }
    const data = JSON.parse(decode(output.slice(0, stream.totalOut))) as Block[];
    let size = 0;
    for (const b of data) {
      if (!Number.isInteger(b.n) || b.n < 1 || b.n > maxBlock || !hashPattern.test(b.h)) throw new Error('分块数据有误');
      size += b.n;
    }
    if (size !== r.bytes) throw new Error('索引与包大小不符');
    return data;
  } finally { await zip.inflateEnd(stream); }
}

// Identical low 16 bits and cut points to packages/direct-tunnel/updates/blocks.go.
function gearTable(): number[] {
  let x = BigInt('0x6a09e667f3bcc909');
  const table: number[] = [];
  for (let i = 0; i < 256; i++) {
    x = BigInt.asUintN(64, x ^ (x << BigInt(13)));
    x ^= x >> BigInt(7);
    x = BigInt.asUintN(64, x ^ (x << BigInt(17)));
    table.push(Number(x & BigInt(65535)));
  }
  return table;
}

async function indexBase(fd: number, size: number): Promise<Map<string, number>> {
  const found = new Map<string, number>();
  const gear = gearTable();
  const input = new ArrayBuffer(512 * 1024);
  const buffer = new Uint8Array(input);
  const chunk = new Uint8Array(maxBlock);
  let pos = 0;
  let length = 0;
  let roll = 0;
  while (pos < size) {
    const count = fs.readSync(fd, input);
    if (count <= 0) throw new Error('旧 HAP 读取中断');
    for (let i = 0; i < count; i++) {
      const value = buffer[i];
      chunk[length++] = value;
      roll = ((roll << 1) + gear[value]) & 65535;
      if (length === maxBlock || (length >= 16 * 1024 && roll === 0)) {
        found.set(await sha(chunk.subarray(0, length)), pos + i + 1 - length);
        length = 0;
        roll = 0;
      }
    }
    pos += count;
    await new Promise<void>((done) => { setTimeout(done, 0); });
  }
  if (length) found.set(await sha(chunk.subarray(0, length)), pos - length);
  return found;
}

function write(fd: number, bytes: ArrayBuffer): void {
  if (fs.writeSync(fd, bytes) !== bytes.byteLength) throw new Error('更新包写入不完整');
}

function header(headers: Object, name: string): string {
  const values = headers as Record<string, string>;
  for (const key of Object.keys(values)) if (key.toLowerCase() === name) return String(values[key]);
  return '';
}

async function reconstruct(r: HapRelease, dir: string, old: string, tmp: string,
  progress: (text: string) => void): Promise<HapResult> {
  const map = await blocks(r);
  const output = fs.openSync(tmp, fs.OpenMode.CREATE | fs.OpenMode.READ_WRITE | fs.OpenMode.TRUNC);
  let base: fs.File | undefined;
  let network = 0;
  let replacedByFull = false;
  try {
    const local = old ? fs.openSync(old, fs.OpenMode.READ_ONLY) : undefined;
    base = local;
    const size = local ? fs.statSync(local.fd).size : 0;
    if (size > 1024 * 1024 * 1024) throw new Error('旧 HAP 太大');
    const index = local ? await indexBase(local.fd, size) : new Map<string, number>();
    const missing: Span[] = [];
    let pos = 0;
    for (const b of map) {
      const source = index.get(b.h);
      if (local && source !== undefined) {
        fs.lseek(local.fd, source);
        fs.lseek(output.fd, pos);
        const buf = new ArrayBuffer(b.n);
        if (fs.readSync(local.fd, buf) !== b.n) throw new Error('旧 HAP 已变化');
        write(output.fd, buf);
      } else {
        const last = missing.length ? missing[missing.length - 1] : undefined;
        if (last && pos - last.end <= 65536) last.end = pos + b.n;
        else missing.push({ start: pos, end: pos + b.n });
      }
      pos += b.n;
    }
    const url = assets + r.tag + '/' + r.name;
    const needed = missing.reduce((sum: number, s: Span) => sum + s.end - s.start, 0);
    for (const span of missing) {
      for (let start = span.start; start < span.end; ) {
        const end = Math.min(start + 4 * 1024 * 1024, span.end);
        progress('正在下载 ' + mib(network) + ' / ' + mib(needed) + ' MiB');
        const resp = await get(url, Math.min(100 * 1024 * 1024, r.bytes), 'bytes=' + start + '-' + (end - 1));
        const bytes = resp.result as ArrayBuffer;
        if (resp.responseCode === 200 && bytes.byteLength === r.bytes) {
          fs.lseek(output.fd, 0);
          write(output.fd, bytes);
          network += bytes.byteLength;
          replacedByFull = true;
          break;
        }
        if (resp.responseCode !== 206 || header(resp.header, 'content-range') !== 'bytes ' + start + '-' + (end - 1) + '/' + r.bytes ||
            bytes.byteLength !== end - start) throw new Error('GitHub 返回的下载范围不符，请重试');
        fs.lseek(output.fd, start);
        write(output.fd, bytes);
        network += bytes.byteLength;
        start = end;
      }
      if (replacedByFull) break;
    }
  } finally {
    if (base) fs.closeSync(base);
    fs.closeSync(output);
  }
  if ((await fileHash.hash(tmp, 'sha256')).toLowerCase() !== r.sha256) throw new Error('最终 HAP 校验失败，请重试');
  const path = dir + '/' + r.name;
  fs.renameSync(tmp, path);
  return { path: path, downloaded: network, reused: replacedByFull ? 0 : r.bytes - network };
}

export function mib(n: number): string { return (n / 1048576).toFixed(2); }

async function prepare(r: HapRelease, cache: string, installed: string, bundleDir: string,
  selected: string, progress: (text: string) => void): Promise<HapResult> {
  const dir = cache + '/piulink-updates';
  if (!fs.accessSync(dir)) fs.mkdirSync(dir);
  const path = dir + '/' + r.name;
  if (fs.accessSync(path) && (await fileHash.hash(path, 'sha256')).toLowerCase() === r.sha256) {
    return { path: path, downloaded: 0, reused: r.bytes };
  }
  const cached = dir + '/PiuLink-HarmonyOS-' + installed + '-unsigned.hap';
  const installedHap = bundleDir + '/entry.hap';
  const old = selected || (fs.accessSync(cached) ? cached : (fs.accessSync(installedHap) ? installedHap : ''));
  const tmp = dir + '/.part-' + util.generateRandomUUID();
  try { return await reconstruct(r, dir, old, tmp, progress); }
  finally { if (fs.accessSync(tmp)) fs.unlinkSync(tmp); }
}

const pending = new Map<string, Promise<HapResult>>();

export async function prepareRelease(r: HapRelease, cache: string, installed: string, bundleDir: string,
  selected: string, progress: (text: string) => void): Promise<HapResult> {
  const key = cache + '/' + r.sha256;
  const active = pending.get(key);
  if (active) return await active;
  const task = prepare(r, cache, installed, bundleDir, selected, progress);
  pending.set(key, task);
  try { return await task; }
  finally { pending.delete(key); }
}
