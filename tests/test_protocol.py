import unittest

from engine.protocol import ProtocolError, parse_message


class TestProtocol(unittest.TestCase):
    def test_valid_state(self):
        msg = parse_message('{"type":"state","instrument":"ES","close":5832.25}')
        self.assertEqual(msg["type"], "state")

    def test_valid_pivot(self):
        msg = parse_message('{"type":"pivot","isHigh":true,"barIndex":12,"price":100.5}')
        self.assertTrue(msg["isHigh"])

    def test_invalid_json(self):
        with self.assertRaises(ProtocolError):
            parse_message("{oops")

    def test_unknown_type(self):
        with self.assertRaises(ProtocolError):
            parse_message('{"type":"blah"}')

    def test_non_object(self):
        with self.assertRaises(ProtocolError):
            parse_message("[1,2,3]")


if __name__ == "__main__":
    unittest.main()
