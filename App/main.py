from kivy.app import App
from kivy.uix.boxlayout import BoxLayout
from kivy.graphics import Color, Rectangle
from kivy.uix.label import Label
from kivy.uix.button import Button
from kivy.uix.gridlayout import GridLayout

class MyBoxLayout(BoxLayout):
    def __init__(self, **kwargs):
        super(MyBoxLayout, self).__init__(**kwargs)

        with self.canvas:
            # Set the background color to white
            Color(1, 1, 1, 1)  # White: R=1, G=1, B=1, Alpha=1 (fully opaque)
            self.rect = Rectangle(pos=self.pos, size=self.size)

        self.bind(pos=self.update_rectangle, size=self.update_rectangle)

        # Create a vertical layout for the label and button grid
        self.vertical_layout = BoxLayout(orientation='vertical', spacing=10, padding=10)

        # Add a label with some text and align it at the top center
        self.label = Label(text="Hello, Kivy!", font_size='24sp', color=(0, 0, 0, 1), halign='center', valign='top')
        self.vertical_layout.add_widget(self.label)

        # Create a grid layout for the buttons (2 rows and 2 columns)
        self.button_grid = GridLayout(rows=2, cols=2, spacing=10, size_hint=(None, None), size=(200, 100))

        # Add buttons to the grid
        for i in range(4):
            button = Button(text=f"Button {i + 1}")
            button.bind(on_press=self.on_button_click)
            self.button_grid.add_widget(button)

        # Add the button grid to the vertical layout
        self.vertical_layout.add_widget(self.button_grid)

        # Add the vertical layout to the main layout
        self.add_widget(self.vertical_layout)

    def update_rectangle(self, *args):
        self.rect.pos = self.pos
        self.rect.size = self.size

    def on_button_click(self, instance):
        self.label.text = f"Button {instance.text} Clicked!"

class MyApp(App):
    def build(self):
        return MyBoxLayout()

if __name__ == '__main__':
    MyApp().run()
