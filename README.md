# PasteTool
Keep watching text copied in clipboard and place it in a queue and paste them one by one by clicking Ctrl + V

程序启动后不会出现窗口，只在任务栏右下角留下一个蓝色 **V** 图标。之后新复制的文字会按顺序排队，Ctrl+V 则按这个队列逐条粘贴。

使用方式：

1. 启动后再复制文字。每次复制都会排到队尾；先复制的先粘贴。
2. 在输入框里按一次 Ctrl+V，就粘贴队列里的第一条，并把这一条移出。再按，就贴下一条。
3. 队列空了再按 Ctrl+V，屏幕右下角会显示「粘贴板内容已空」，1 秒后自动消失。
4. 右键图标可以看当前有几条，点「退出」关闭程序。

在项目目录执行 `dotnet run`，或在 Visual Studio 里按 F5 即可运行。如果图标被收进任务栏的 **^** 里，把它拖到外面就能一直看到。

程序开着时，Ctrl+V 只走这个队列。按住不放只会贴一条，松开后再按才会贴下一条。目标程序若以管理员身份运行，PasteTool 也需要用管理员身份启动，否则粘贴送不进去。