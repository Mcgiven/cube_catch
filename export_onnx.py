import torch

# 你的 .pt 模型路径
pt_path = r"F:\MLprogram\CatcherGame\results\catcher_v4\Catcher\Catcher-499996.pt"
# 要生成的 .onnx 路径
onnx_path = r"F:\MLprogram\CatcherGame\results\catcher_v4\Catcher\Catcher.onnx"

print("正在加载模型...")
# 加载 TorchScript 模型
model = torch.jit.load(pt_path)
model.eval()

# 创建一个虚拟输入（你的观测空间大小是 9）
dummy_input = torch.randn(1, 9)

print("正在导出为 ONNX...")
# 关键：加上 dynamo=False，强制使用旧版导出器，避免触发 onnxscript 报错
torch.onnx.export(
    model,
    dummy_input,
    onnx_path,
    export_params=True,
    opset_version=9,
    do_constant_folding=True,
    input_names=['obs_0'],
    output_names=['continuous_actions'],
    dynamo=False
)
print("导出成功！文件已生成在：", onnx_path)