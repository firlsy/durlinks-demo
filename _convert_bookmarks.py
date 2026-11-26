# -*- coding: utf-8 -*-
"""把『书签地球_书签导出_全量去重.xlsx / 全部书签(去重)』按导入模板 14 列转换。"""
import sys, io, re, os, time
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import openpyxl
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.utils import get_column_letter

SRC = r"E:\DATA\workbuddy\program-durlinks-demos\书签地球_书签导出_全量去重.xlsx"
OUT = r"E:\DATA\workbuddy\program-durlinks-demos\书签导入_转换后_83332条.xlsx"

TPL_HEADER = ['序号', '链接形式', '分类', '名称', '链接', '提取码', '网盘过期时间',
              '平台', '版本', '附属链接', '添加时间', '标签', '备注', 'AI摘要']

PAN_DOMAINS = ('pan.baidu.com', 'lanzou', 'liangxun', 'aliyundrive.com', 'pan.quark.cn',
               '123pan.com', '123684.com', 'caiyun.139.com', 'wenshushu.cn', '115.com',
               'xunlei.com', 'weiyun.com', 'lensho.cn', 'yun.baidu.com', 'pan.xiaomiquan',
               'jjg Shares', 'drive.google.com', 'dropbox.com', 'onedrive.live.com',
               'www.123912.com', 'pan.huang1111.cn', 'www.123865.com', 'www.123pan.cn',
               'lanzoux.com', 'lanzui.com', 'lanzouw.com', 'lanzoui.com', 'lanzoug.com',
               'kdocs.cn', 'www.aliyundrive.com', 'alipan.com')
DL_EXT = re.compile(r'\.(exe|msi|zip|rar|7z|apk|dmg|pkg|tar\.gz|tgz|iso|deb|rpm|appimage)([?#]|$)', re.I)

def sval(v):
    return '' if v is None else str(v).strip()

def link_form(url, domain, name):
    u = url.lower()
    d = domain.lower()
    if 'magnet:' in u or u.endswith('.torrent') or '.torrent?' in u:
        return '磁力'
    if any(p in d for p in PAN_DOMAINS):
        return '网盘'
    if DL_EXT.search(u.split('?')[0]):
        return '下载直链'
    return '官网普通网址'

CODE_RE = re.compile(r'(?:提取码|密码|访问码|口令)\s*[:：]?\s*([A-Za-z0-9]{4,8})')
def extract_code(path, name):
    m = CODE_RE.search(sval(path) + ' ' + sval(name))
    return m.group(1) if m else ''

PLATFORMS = [('windows', 'Windows'), ('win客户端', 'Windows'), (' android', 'Android'),
             ('安卓', 'Android'), ('apple', 'iOS'), (' ios', 'iOS'), ('iphone', 'iOS'),
             ('mac', 'Mac'), ('linux', 'Linux'), ('跨平台', '跨平台')]
def detect_platform(name):
    n = (' ' + sval(name).lower() + ' ')
    found = []
    for key, val in PLATFORMS:
        if key in n and val not in found:
            found.append(val)
    return '/'.join(found[:3])

VER_RE = re.compile(r'(?<!\d)v?(\d{1,3}\.\d{1,3}(?:\.\d{1,3}){0,2})(?!\d)')
def detect_version(name):
    n = sval(name)
    if re.search(r'\bv\d', n, re.I) or '版本' in n:
        m = VER_RE.search(n)
        return m.group(1) if m else ''
    return ''

def make_tags(cat, path, domain, root_domain):
    tags = []
    if cat: tags.append(cat)
    p = sval(path)
    if p:
        p = re.sub(r'^\d+[\.\s_-]*', '', p)  # 去掉前导编号
        p = re.split(r'\s*[>＞]\s*', p)[0].strip(' -_·')
        if p and len(p) <= 20: tags.append(p)
    if root_domain: tags.append(root_domain)
    elif domain: tags.append(domain)
    return ';'.join(dict.fromkeys(tags))

def make_summary(name, cat, domain, times, collection, sharer, path):
    name = sval(name)
    if len(name) > 60: name = name[:60] + '…'
    parts = []
    if name:
        parts.append(f'{name}' + (f'（{domain}）' if domain else ''))
    else:
        parts.append(domain or '该资源')
    mid = f'属于「{cat}」分类' if cat else '未分类资源'
    if path and len(sval(path)) <= 24:
        mid += f'，原始归类为「{sval(path)}」'
    parts.append(mid)
    tail = []
    try:
        t = int(times)
    except (TypeError, ValueError):
        t = 0
    if t > 1:
        tail.append(f'被收藏{t}次，属热门条目')
    if collection and sval(collection) not in ('官方导出',):
        tail.append(f'收录自「{sval(collection)}」书签集')
    if sharer:
        tail.append(f'分享者：{sval(sharer)}')
    if tail:
        parts.append('，'.join(tail))
    return ''.join(parts[:1]) + '，' + parts[1] + ('，' + '；'.join(tail) if tail else '') + '。'

def main():
    t0 = time.time()
    wb = openpyxl.load_workbook(SRC, read_only=True, data_only=True)
    ws = wb['全部书签(去重)']
    rows = ws.iter_rows(values_only=True)
    header = list(next(rows))
    idx = {h: i for i, h in enumerate(header)}
    C = lambda r, k: r[idx[k]] if k in idx and idx[k] < len(r) else ''

    out = openpyxl.Workbook(write_only=True)
    ows = out.create_sheet('导入模板')
    ows.append(TPL_HEADER)
    n = 0
    form_counter = {}
    for r in rows:
        url = sval(C(r, '网址'))
        if not url:
            continue
        n += 1
        cat = sval(C(r, '归类'))
        pathv = C(r, '原始分类路径')
        name = sval(C(r, '名称'))
        domain = sval(C(r, '域名'))
        root = sval(C(r, '主域名'))
        times = C(r, '出现次数')
        collection = sval(C(r, '来源集合'))
        sharer = sval(C(r, '分享者'))
        form = link_form(url, domain, name)
        form_counter[form] = form_counter.get(form, 0) + 1
        ows.append([
            n,
            form,
            cat or '未分类',
            name or domain or url[:60],
            url,
            extract_code(pathv, name) if form == '网盘' else '',
            '',
            detect_platform(name) if form == '下载直链' else detect_platform(name),
            detect_version(name),
            '',
            '',  # 添加时间留空 -> 导入日
            make_tags(cat, pathv, domain, root),
            '；'.join(x for x in [f'来源集合：{collection}' if collection else '',
                                  f'分享者：{sharer}' if sharer else '',
                                  f'出现次数：{times}' if times else ''] if x),
            make_summary(name, cat, domain, times, collection, sharer, pathv),
        ])
    wb.close()
    out.save(OUT)
    print('written rows:', n)
    print('link forms:', form_counter)
    print('output:', OUT)
    print('size MB:', round(os.path.getsize(OUT) / 1024 / 1024, 2))
    print('elapsed sec:', round(time.time() - t0, 1))

if __name__ == '__main__':
    main()
