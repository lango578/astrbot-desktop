"""验收探针：只为证明「面板上传 zip → 重启 → 插件真的被加载」这条路是通的。

它不做任何事。加载成功会打一行日志，验收脚本就抓那一行——上一版这里用掉了
安卓的掉线修复插件，结果插件缺主类导致加载失败，而补丁因为 import 副作用侥幸生效，
测试照样绿；所以断言必须看「加载成功」而不是看副作用。
"""

from astrbot.api import logger
from astrbot.api.star import Context, Star


class UploadProbe(Star):
    """AstrBot 会自动识别继承 Star 的类作为插件主类。"""

    def __init__(self, context: Context):
        super().__init__(context)
        logger.info("[上传验收] 探针插件已加载")
