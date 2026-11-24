package com.limelight.piulink.ssh;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;
import com.jcraft.jsch.JSch;
import com.jcraft.jsch.KeyPair;
import java.io.*;
import java.security.KeyStore;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Private material is encrypted with a non-exportable Android Keystore wrapping key. */
final class DeviceSshIdentity implements AutoCloseable {
    final byte[] privateKey;
    final String publicKey;
    private DeviceSshIdentity(byte[] key, String pub) { privateKey = key; publicKey = pub; }
    static synchronized DeviceSshIdentity load(Context context) throws Exception {
        if (android.os.Build.VERSION.SDK_INT < 23)
            throw new IOException("设备密钥登录需要 Android 6.0 或更新版本。");
        final String alias = "piulink-ssh-device-v1";
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        AtomicFile file = new AtomicFile(new File(context.getNoBackupFilesDir(), "ssh-device-v1"));
        if (!store.containsAlias(alias)) {
            // Never silently replace an identity whose wrapping key was lost.
            if (file.getBaseFile().exists()) throw new IOException("SSH device key unavailable; restore device authorization");
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        SecretKey wrapping = (SecretKey) store.getKey(alias, null);
        byte[] plain;
        if (file.getBaseFile().exists()) {
            try (DataInputStream input = new DataInputStream(file.openRead())) {
                if (input.readInt() != 1) throw new IOException("Unsupported SSH key format");
                byte[] iv = new byte[12]; input.readFully(iv);
                int length = input.readInt();
                if (length < 16 || length > 65536) throw new IOException("Invalid SSH key file");
                byte[] encrypted = new byte[length]; input.readFully(encrypted);
                Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
                cipher.init(Cipher.DECRYPT_MODE, wrapping, new GCMParameterSpec(128, iv));
                plain = cipher.doFinal(encrypted);
            }
        } else {
            KeyPair pair = KeyPair.genKeyPair(new JSch(), KeyPair.RSA, 3072);
            try {
                ByteArrayOutputStream output = new ByteArrayOutputStream(); pair.writePrivateKey(output);
                plain = output.toByteArray();
            } finally { pair.dispose(); }
            try {
                Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, wrapping);
                byte[] encrypted = cipher.doFinal(plain);
                FileOutputStream stream = file.startWrite();
                try {
                    DataOutputStream output = new DataOutputStream(stream);
                    output.writeInt(1); output.write(cipher.getIV()); output.writeInt(encrypted.length); output.write(encrypted); output.flush();
                    file.finishWrite(stream);
                } catch (Exception e) { file.failWrite(stream); throw e; }
            } catch (Exception e) { Arrays.fill(plain, (byte) 0); throw e; }
        }
        try {
            KeyPair pair = KeyPair.load(new JSch(), plain, null);
            try {
                ByteArrayOutputStream pub = new ByteArrayOutputStream(); pair.writePublicKey(pub, "PiuLink-phone");
                return new DeviceSshIdentity(plain, pub.toString("UTF-8").trim());
            } finally { pair.dispose(); }
        } catch (Exception e) { Arrays.fill(plain, (byte) 0); throw e; }
    }
    @Override public void close() { Arrays.fill(privateKey, (byte) 0); }
}
