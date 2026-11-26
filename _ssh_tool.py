# -*- coding: utf-8 -*-
"""远端执行/上传工具：凭据从 secrets.env 运行时读取，不落明文。"""
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import paramiko

ROOT = r"E:\DATA\workbuddy\program-durlinks-demos"
ENV = os.path.join(ROOT, "secrets.env")


def load_env(path):
    d = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            d[k.strip()] = v.strip()
    return d


def connect():
    env = load_env(ENV)
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(
        env["SERVER_HOST"],
        port=int(env.get("SERVER_SSH_PORT", 22)),
        username=env.get("SERVER_USER", "root"),
        password=env["SERVER_PASSWORD"],
        timeout=20,
        banner_timeout=20,
        auth_timeout=20,
    )
    return c


def run(cmd, timeout=600):
    c = connect()
    try:
        stdin, stdout, stderr = c.exec_command(cmd, timeout=timeout)
        out = stdout.read().decode("utf-8", "ignore")
        err = stderr.read().decode("utf-8", "ignore")
        code = stdout.channel.recv_exit_status()
        if out:
            print(out, end="" if out.endswith("\n") else "\n")
        if err.strip():
            print("[stderr]\n" + err)
        print("[exit]", code)
        return code
    finally:
        c.close()


def upload(local, remote, timeout=900):
    """通过 exec 通道 cat > 写入，绕开 sftp 子系统路径限制。"""
    c = connect()
    try:
        stdin, stdout, stderr = c.exec_command(f"cat > '{remote}'", timeout=timeout)
        with open(local, "rb") as f:
            while True:
                chunk = f.read(1024 * 512)
                if not chunk:
                    break
                stdin.write(chunk)
        stdin.channel.shutdown_write()
        err = stderr.read().decode("utf-8", "ignore")
        code = stdout.channel.recv_exit_status()
        if code != 0:
            print("[upload failed]", code, err[:300])
        else:
            print("[uploaded]", os.path.basename(local), "->", remote)
    finally:
        c.close()


if __name__ == "__main__":
    if sys.argv[1] == "run":
        run(sys.argv[2])
    elif sys.argv[1] == "upload":
        upload(sys.argv[2], sys.argv[3])
