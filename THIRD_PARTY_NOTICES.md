# 第三方读屏接口说明

根目录的 MIT 许可证适用于本项目自有源码和文档，不改变第三方 DLL 的权利归属与许可条件。`src/WeChatMessageReaderAssistant/WeChatMessageReaderAssistant.App/Native/` 中的 DLL 来自各自的项目或厂商，并未以 MIT 重新授权。

- `ZDSRAPI_x64.dll`、`ZDSRAPI.ini`：争渡读屏 API。官方文档说明应用可以调用安装目录里的 DLL，也可复制到应用目录；使用及再分发仍应遵守厂商条款。资料：https://www.zdsr.com/docs/api/zdsr-api/
- `BoyCtrl-x64.dll`、`BoyCtrl.conf`、`byctrl-x64.dll`、`byctrl.conf`：保益读屏接口。接口资料：https://github.com/sig-a11y/BoyCtrl-API 。仓库公开可见不代表这些 DLL 获得 MIT 许可。
- `nvdaControllerClient64.dll`：NVDA Controller Client，LGPL 2.1。资料和许可：https://github.com/nvaccess/nvda/blob/master/extras/controllerClient/readme.md
- `prism.dll`、`tolk.dll`：第三方读屏兼容接口。它们不属于本项目 MIT 授权范围，相关上游材料位于各自项目。

如果计划重新分发这些 DLL，应分别核实相应上游的许可要求。项目功能代码仍可按 MIT 条款使用、修改和再发布。