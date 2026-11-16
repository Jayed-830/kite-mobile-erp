// Offline PIN check for emergency mode: the snapshot holds the server's Argon2id PIN hashes (PHC strings) of the
// users who may sell in this branch; the clear PIN never leaves this process.
import { argon2Verify } from 'hash-wasm';

export async function verifyPin(pin: string, encoded: string | null | undefined): Promise<boolean> {
  if (!encoded || !/^\d{4,8}$/.test(pin) || !encoded.startsWith('$argon2id$')) return false;
  try { return await argon2Verify({ password: pin, hash: encoded }); } catch { return false; }
}
